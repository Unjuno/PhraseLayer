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

            hitInfoType = nativeFuncsType.GetNestedType(HitInfoTypeName, BindingFlags.Public | BindingFlags.NonPublic);
            hitPointType = nativeFuncsType.GetNestedType(HitPointTypeName, BindingFlags.Public | BindingFlags.NonPublic);
            createRaycasterField = GetNativeDelegateField("CreateEnvironmentRaycaster");
            destroyRaycasterField = GetNativeDelegateField("DestroyEnvironmentRaycaster");
            raycasterStatusField = GetNativeDelegateField("EnvironmentRaycasterStatus");
            raycastEnvironmentField = GetNativeDelegateField("RaycastEnvironment");
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

            var raycast = GetDelegate(raycastEnvironmentField);
            if (raycast == null)
                return false;

            try
            {
                var origin = ToUnity(ray.Origin);
                var direction = Normalize(ToUnity(ray.Direction));

                var hitInfo = Activator.CreateInstance(hitInfoType);
                SetField(hitInfo, "startPoint", origin);
                SetField(hitInfo, "direction", direction);
                SetField(hitInfo, "filterCount", 0u);
                SetField(hitInfo, "maxDistance", maxDistanceMeters);

                var hitPoint = Activator.CreateInstance(hitPointType);
                var arguments = new[] { hitInfo, hitPoint };
                var result = raycast.DynamicInvoke(arguments);
                if (ToInt32(result) != ResultSuccess)
                    return false;

                hitPoint = arguments[1];
                if (ToInt32(ReadField(hitPoint, "status")) != RaycastStatusHit)
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
            catch (TargetInvocationException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (MemberAccessException)
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
            if (ownsEnvironmentRaycaster && TryGetRaycasterStatus(out var statusValue) && statusValue == RaycasterReady)
            {
                var destroy = GetDelegate(destroyRaycasterField);
                if (destroy != null)
                {
                    try
                    {
                        destroy.DynamicInvoke();
                    }
                    catch (TargetInvocationException)
                    {
                        // The native boundary is optional. Shutdown must not make the fallback renderer fail.
                    }
                    catch (ArgumentException)
                    {
                    }
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

            var create = GetDelegate(createRaycasterField);
            if (create == null)
                return false;

            try
            {
                var result = create.DynamicInvoke();
                if (ToInt32(result) != ResultSuccess)
                    return false;
                creationRequested = true;
                ownsEnvironmentRaycaster = true;
                // Creation is asynchronous. A later observation will see Ready and perform the real raycast.
                return false;
            }
            catch (TargetInvocationException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private bool TryGetRaycasterStatus(out int statusValue)
        {
            statusValue = int.MinValue;
            var status = GetDelegate(raycasterStatusField);
            if (status == null)
                return false;

            try
            {
                statusValue = ToInt32(status.DynamicInvoke());
                return statusValue != int.MinValue;
            }
            catch (TargetInvocationException)
            {
                return false;
            }
            catch (ArgumentException)
            {
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
            var scaleProperty = typeof(Transform).GetProperty("lossyScale", BindingFlags.Instance | BindingFlags.Public);
            if (scaleProperty == null || !(scaleProperty.GetValue(trackingOrigin, null) is Vector3 scale))
                return false;
            return Math.Abs(scale.x - 1.0) <= TransformTolerance &&
                   Math.Abs(scale.y - 1.0) <= TransformTolerance &&
                   Math.Abs(scale.z - 1.0) <= TransformTolerance;
        }

        private FieldInfo GetNativeDelegateField(string fieldName)
        {
            return nativeFuncsType.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static Delegate GetDelegate(FieldInfo field)
        {
            return field == null ? null : field.GetValue(null) as Delegate;
        }

        private static void SetField(object value, string fieldName, object fieldValue)
        {
            if (value == null)
                throw new InvalidOperationException("MRUK native raycast struct could not be created.");
            var field = value.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(value.GetType().FullName, fieldName);
            field.SetValue(value, fieldValue);
        }

        private static object ReadField(object value, string fieldName)
        {
            if (value == null)
                throw new InvalidOperationException("MRUK native raycast result was null.");
            var field = value.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(value.GetType().FullName, fieldName);
            return field.GetValue(value);
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
                var type = assemblies[index].GetType(fullName, false);
                if (type != null)
                    return type;
            }
            return null;
        }

        private static bool TryReadVector3(object value, string memberName, out Vector3 result)
        {
            result = default(Vector3);
            if (value == null)
                return false;

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

        private static int ToInt32(object value)
        {
            return value == null ? int.MinValue : Convert.ToInt32(value);
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
