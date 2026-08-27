using System;
using System.Reflection;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Optional Meta environment-raycast adapter that talks directly to MRUK's native environment-raycaster
    /// delegates through reflection. PhraseLayer deliberately does not instantiate Meta's
    /// EnvironmentRaycastManager component because that component emits an MRUK telemetry event from Start().
    ///
    /// The native raycaster consumes tracking-space coordinates. PhraseLayer's committed Read MVP keeps its OpenXR
    /// tracking origin at Unity world origin, so the Passthrough Camera ray can cross this boundary without inventing
    /// an additional transform. If the application later introduces a moved/scaled tracking origin, this adapter
    /// fails closed until that coordinate-space contract is implemented explicitly.
    ///
    /// The adapter never requests permission itself. Missing permission, an uninitialized MRUK native layer, a
    /// creating/not-ready raycaster, or any reflected API mismatch simply returns no hit so the caller can fall back
    /// to ordinary Unity collider geometry and ultimately the viewport overlay.
    /// </summary>
    public sealed class MetaEnvironmentDepthSurfaceRaycaster : ISurfaceRaycaster, IDisposable
    {
        public const string ScenePermission = "com.oculus.permission.USE_SCENE";

        private const string NativeFuncsTypeName = "Meta.XR.MRUtilityKit.MRUKNativeFuncs";
        private const string HitInfoTypeName = "MrukEnvironmentRaycastHitPointGetInfo";
        private const string HitPointTypeName = "MrukEnvironmentRaycastHitPoint";
        private const int ResultSuccess = 0;
        private const int RaycasterStopped = 0;
        private const int RaycasterCreating = 1;
        private const int RaycasterReady = 2;
        private const int RaycastStatusHit = 1;
        private const double TransformTolerance = 0.00001;

        private readonly float maxDistanceMeters;
        private readonly Transform trackingOrigin;
        private readonly Type nativeFuncsType;
        private readonly Type hitInfoType;
        private readonly Type hitPointType;
        private readonly FieldInfo createRaycasterField;
        private readonly FieldInfo destroyRaycasterField;
        private readonly FieldInfo raycasterStatusField;
        private readonly FieldInfo raycastEnvironmentField;

        private bool creationRequested;
        private bool ownsEnvironmentRaycaster;
        private bool disposed;

        public MetaEnvironmentDepthSurfaceRaycaster(GameObject owner, float maxDistanceMeters = 10f)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (float.IsNaN(maxDistanceMeters) || float.IsInfinity(maxDistanceMeters) || maxDistanceMeters <= 0f)
                throw new ArgumentOutOfRangeException(nameof(maxDistanceMeters));
            this.maxDistanceMeters = maxDistanceMeters;
            trackingOrigin = owner.transform;

            nativeFuncsType = ResolveType(NativeFuncsTypeName);
            if (nativeFuncsType == null)
                return;

            try
            {
                hitInfoType = nativeFuncsType.GetNestedType(HitInfoTypeName, BindingFlags.Public | BindingFlags.NonPublic);
                hitPointType = nativeFuncsType.GetNestedType(HitPointTypeName, BindingFlags.Public | BindingFlags.NonPublic);
                createRaycasterField = GetNativeDelegateField("CreateEnvironmentRaycaster");
                destroyRaycasterField = GetNativeDelegateField("DestroyEnvironmentRaycaster");
                raycasterStatusField = GetNativeDelegateField("EnvironmentRaycasterStatus");
                raycastEnvironmentField = GetNativeDelegateField("RaycastEnvironment");
            }
            catch (Exception exception) when (IsRecoverableBoundaryException(exception))
            {
                // A package/API mismatch disables only this optional surface source. Physics/viewport remain valid.
            }
        }

        public bool IsApiAvailable =>
            nativeFuncsType != null &&
            hitInfoType != null &&
            hitPointType != null &&
            createRaycasterField != null &&
            destroyRaycasterField != null &&
            raycasterStatusField != null &&
            raycastEnvironmentField != null;

        public bool TryRaycast(SpatialRay ray, out SurfaceHit hit)
        {
            hit = default(SurfaceHit);
            if (disposed || !IsApiAvailable || !HasSpatialPermission() || !IsIdentityTrackingOrigin() || !TryEnsureRaycasterReady())
                return false;
            if (!TryGetDelegate(raycastEnvironmentField, out var raycast))
                return false;

            try
            {
                var origin = ToUnity(ray.Origin);
                var direction = Normalize(ToUnity(ray.Direction));

                var hitInfo = Activator.CreateInstance(hitInfoType);
                if (!TrySetField(hitInfo, "startPoint", origin) ||
                    !TrySetField(hitInfo, "direction", direction) ||
                    !TrySetField(hitInfo, "filterCount", 0u) ||
                    !TrySetField(hitInfo, "maxDistance", maxDistanceMeters))
                    return false;

                var hitPoint = Activator.CreateInstance(hitPointType);
                var arguments = new[] { hitInfo, hitPoint };
                var invocationResult = raycast.DynamicInvoke(arguments);
                if (!TryConvertToInt32(invocationResult, out var resultValue) || resultValue != ResultSuccess)
                    return false;

                hitPoint = arguments[1];
                if (!TryReadField(hitPoint, "status", out var status) ||
                    !TryConvertToInt32(status, out var hitStatus) ||
                    hitStatus != RaycastStatusHit)
                    return false;
                if (!TryReadVector3(hitPoint, "point", out var point) ||
                    !TryReadVector3(hitPoint, "normal", out var normal))
                    return false;

                normal = Normalize(normal);
                var distance = Distance(origin, point);
                if (distance > maxDistanceMeters)
                    return false;

                hit = new SurfaceHit(ToSpatial(point), ToSpatial(normal), distance);
                return true;
            }
            catch (Exception exception) when (IsRecoverableBoundaryException(exception))
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            // Mirror MRUK's own shutdown discipline: the native destroy call is valid only for a Ready handle.
            // If creation is still asynchronous, leave the package-global handle alone rather than destroying an
            // in-flight resource. A later MRUK/Quest adapter can reuse that global handle.
            if (ownsEnvironmentRaycaster &&
                TryGetRaycasterStatus(out var statusValue) &&
                statusValue == RaycasterReady &&
                TryGetDelegate(destroyRaycasterField, out var destroy))
            {
                try
                {
                    destroy.DynamicInvoke();
                }
                catch (Exception exception) when (IsRecoverableBoundaryException(exception))
                {
                    // The native boundary is optional. Shutdown must not make the fallback renderer fail.
                }
            }

            creationRequested = false;
            ownsEnvironmentRaycaster = false;
        }

        private bool TryEnsureRaycasterReady()
        {
            if (!TryGetRaycasterStatus(out var statusValue))
                return false;

            if (statusValue == RaycasterReady)
                return true;
            if (statusValue == RaycasterCreating)
            {
                creationRequested = true;
                return false;
            }
            if (statusValue != RaycasterStopped || creationRequested)
                return false;
            if (!TryGetDelegate(createRaycasterField, out var create))
                return false;

            try
            {
                var invocationResult = create.DynamicInvoke();
                if (!TryConvertToInt32(invocationResult, out var resultValue) || resultValue != ResultSuccess)
                    return false;
                creationRequested = true;
                ownsEnvironmentRaycaster = true;
                // Creation is asynchronous. A later observation will see Ready and perform the real raycast.
                return false;
            }
            catch (Exception exception) when (IsRecoverableBoundaryException(exception))
            {
                return false;
            }
        }

        private bool TryGetRaycasterStatus(out int statusValue)
        {
            statusValue = int.MinValue;
            if (!TryGetDelegate(raycasterStatusField, out var status))
                return false;

            try
            {
                return TryConvertToInt32(status.DynamicInvoke(), out statusValue);
            }
            catch (Exception exception) when (IsRecoverableBoundaryException(exception))
            {
                statusValue = int.MinValue;
                return false;
            }
        }

        private bool IsIdentityTrackingOrigin()
        {
            if (trackingOrigin == null)
                return false;

            var position = trackingOrigin.localPosition;
            var rotation = trackingOrigin.localRotation;
            if (Math.Abs(position.x) > TransformTolerance ||
                Math.Abs(position.y) > TransformTolerance ||
                Math.Abs(position.z) > TransformTolerance ||
                Math.Abs(rotation.x) > TransformTolerance ||
                Math.Abs(rotation.y) > TransformTolerance ||
                Math.Abs(rotation.z) > TransformTolerance ||
                Math.Abs(Math.Abs(rotation.w) - 1.0) > TransformTolerance)
                return false;

            // lossyScale is reflected so the host compile harness does not need to model the full Unity Transform API.
            try
            {
                var scaleProperty = typeof(Transform).GetProperty("lossyScale", BindingFlags.Instance | BindingFlags.Public);
                if (scaleProperty == null || !(scaleProperty.GetValue(trackingOrigin, null) is Vector3 scale))
                    return false;
                return Math.Abs(scale.x - 1.0) <= TransformTolerance &&
                       Math.Abs(scale.y - 1.0) <= TransformTolerance &&
                       Math.Abs(scale.z - 1.0) <= TransformTolerance;
            }
            catch (Exception exception) when (IsRecoverableBoundaryException(exception))
            {
                return false;
            }
        }

        private FieldInfo GetNativeDelegateField(string fieldName)
        {
            return nativeFuncsType.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static bool TryGetDelegate(FieldInfo field, out Delegate value)
        {
            value = null;
            if (field == null)
                return false;

            try
            {
                value = field.GetValue(null) as Delegate;
                return value != null;
            }
            catch (Exception exception) when (IsRecoverableBoundaryException(exception))
            {
                return false;
            }
        }

        private static bool TrySetField(object value, string fieldName, object fieldValue)
        {
            if (value == null)
                return false;

            try
            {
                var field = value.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                    return false;
                field.SetValue(value, fieldValue);
                return true;
            }
            catch (Exception exception) when (IsRecoverableBoundaryException(exception))
            {
                return false;
            }
        }

        private static bool TryReadField(object value, string fieldName, out object fieldValue)
        {
            fieldValue = null;
            if (value == null)
                return false;

            try
            {
                var field = value.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                    return false;
                fieldValue = field.GetValue(value);
                return true;
            }
            catch (Exception exception) when (IsRecoverableBoundaryException(exception))
            {
                return false;
            }
        }

        private static bool HasSpatialPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return UnityEngine.Android.Permission.HasUserAuthorizedPermission(ScenePermission);
#else
            return true;
#endif
        }

        private static Type ResolveType(string fullName)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var index = 0; index < assemblies.Length; index++)
            {
                try
                {
                    var type = assemblies[index].GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch (Exception exception) when (IsRecoverableBoundaryException(exception))
                {
                    // Continue searching. An optional package boundary must not block the rest of Read Mode.
                }
            }
            return null;
        }

        private static bool TryReadVector3(object value, string memberName, out Vector3 result)
        {
            result = default(Vector3);
            if (value == null)
                return false;

            try
            {
                var type = value.GetType();
                var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.GetValue(value) is Vector3 fieldValue)
                {
                    result = fieldValue;
                    return true;
                }

                var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.GetValue(value, null) is Vector3 propertyValue)
                {
                    result = propertyValue;
                    return true;
                }

                return false;
            }
            catch (Exception exception) when (IsRecoverableBoundaryException(exception))
            {
                return false;
            }
        }

        private static bool TryConvertToInt32(object value, out int result)
        {
            result = int.MinValue;
            if (value == null)
                return false;

            try
            {
                result = Convert.ToInt32(value);
                return true;
            }
            catch (InvalidCastException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static bool IsRecoverableBoundaryException(Exception exception)
        {
            return exception is TargetInvocationException ||
                   exception is TargetException ||
                   exception is AmbiguousMatchException ||
                   exception is MemberAccessException ||
                   exception is ArgumentException ||
                   exception is InvalidOperationException ||
                   exception is InvalidCastException ||
                   exception is FormatException ||
                   exception is OverflowException ||
                   exception is TypeLoadException ||
                   exception is NotSupportedException;
        }

        private static Vector3 Normalize(Vector3 value)
        {
            var magnitude = Math.Sqrt((value.x * value.x) + (value.y * value.y) + (value.z * value.z));
            if (magnitude <= 0.0)
                throw new InvalidOperationException("Spatial ray or surface normal must remain non-zero at the Meta environment boundary.");
            return new Vector3(
                (float)(value.x / magnitude),
                (float)(value.y / magnitude),
                (float)(value.z / magnitude));
        }

        private static double Distance(Vector3 left, Vector3 right)
        {
            var dx = left.x - right.x;
            var dy = left.y - right.y;
            var dz = left.z - right.z;
            return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        private static Vector3 ToUnity(SpatialVector3 value)
        {
            return new Vector3((float)value.X, (float)value.Y, (float)value.Z);
        }

        private static SpatialVector3 ToSpatial(Vector3 value)
        {
            return new SpatialVector3(value.x, value.y, value.z);
        }
    }
}
