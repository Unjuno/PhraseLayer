using System;
using System.Reflection;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Optional Meta Environment Depth adapter resolved through reflection so PhraseLayer keeps the Core/runtime
    /// assembly boundary independent from a concrete Meta XR assembly reference.
    ///
    /// The adapter never requests permission by itself. It activates only when Spatial Data permission has already
    /// been granted, EnvironmentRaycastManager exists in the installed MRUK package, and the device reports support.
    /// Any unavailable/not-ready/error state fails closed so callers can fall back to collider or viewport placement.
    /// </summary>
    public sealed class MetaEnvironmentDepthSurfaceRaycaster : ISurfaceRaycaster
    {
        public const string ScenePermission = "com.oculus.permission.USE_SCENE";
        private const string ManagerTypeName = "Meta.XR.EnvironmentRaycastManager";
        private const string HitTypeName = "Meta.XR.EnvironmentRaycastHit";

        private readonly GameObject owner;
        private readonly float maxDistanceMeters;
        private readonly Type managerType;
        private readonly Type hitType;
        private readonly PropertyInfo isSupportedProperty;
        private readonly MethodInfo raycastWithDistance;
        private readonly MethodInfo raycastWithoutDistance;
        private Component manager;

        public MetaEnvironmentDepthSurfaceRaycaster(GameObject owner, float maxDistanceMeters = 10f)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            if (float.IsNaN(maxDistanceMeters) || float.IsInfinity(maxDistanceMeters) || maxDistanceMeters <= 0f)
                throw new ArgumentOutOfRangeException(nameof(maxDistanceMeters));
            this.maxDistanceMeters = maxDistanceMeters;

            managerType = ResolveType(ManagerTypeName);
            hitType = ResolveType(HitTypeName);
            if (managerType == null || hitType == null)
                return;

            isSupportedProperty = managerType.GetProperty("IsSupported", BindingFlags.Instance | BindingFlags.Public);
            var hitByRef = hitType.MakeByRefType();
            raycastWithDistance = managerType.GetMethod(
                "Raycast",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Ray), hitByRef, typeof(float) },
                null);
            raycastWithoutDistance = managerType.GetMethod(
                "Raycast",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Ray), hitByRef },
                null);
        }

        public bool IsApiAvailable =>
            managerType != null &&
            hitType != null &&
            (raycastWithDistance != null || raycastWithoutDistance != null);

        public bool TryRaycast(SpatialRay ray, out SurfaceHit hit)
        {
            hit = default(SurfaceHit);
            if (!IsApiAvailable || !HasSpatialPermission() || !EnsureManager())
                return false;

            try
            {
                if (isSupportedProperty != null)
                {
                    var supportedValue = isSupportedProperty.GetValue(manager, null);
                    if (supportedValue is bool supported && !supported)
                        return false;
                }

                var unityRay = new Ray(ToUnity(ray.Origin), Normalize(ToUnity(ray.Direction)));
                var boxedHit = Activator.CreateInstance(hitType);
                object[] arguments;
                MethodInfo method;
                if (raycastWithDistance != null)
                {
                    method = raycastWithDistance;
                    arguments = new[] { (object)unityRay, boxedHit, maxDistanceMeters };
                }
                else
                {
                    method = raycastWithoutDistance;
                    arguments = new[] { (object)unityRay, boxedHit };
                }

                var successValue = method.Invoke(manager, arguments);
                if (!(successValue is bool success) || !success)
                    return false;

                boxedHit = arguments[1];
                if (!TryReadVector3(boxedHit, "point", out var point) ||
                    !TryReadVector3(boxedHit, "normal", out var normal))
                    return false;

                var distance = Distance(ToUnity(ray.Origin), point);
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
        }

        private bool EnsureManager()
        {
            if (manager != null)
                return true;

            var components = Resources.FindObjectsOfTypeAll<Component>();
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                if (component == null || component.gameObject == null)
                    continue;
                if (!ReferenceEquals(component.gameObject, owner))
                    continue;
                if (!managerType.IsInstanceOfType(component))
                    continue;
                manager = component;
                return true;
            }

            var addComponent = typeof(GameObject).GetMethod(
                "AddComponent",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Type) },
                null);
            if (addComponent == null)
                return false;

            manager = addComponent.Invoke(owner, new object[] { managerType }) as Component;
            return manager != null;
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
            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
            if (field != null && field.GetValue(value) is Vector3 fieldValue)
            {
                result = fieldValue;
                return true;
            }

            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.GetValue(value, null) is Vector3 propertyValue)
            {
                result = propertyValue;
                return true;
            }

            return false;
        }

        private static Vector3 Normalize(Vector3 value)
        {
            var magnitude = Math.Sqrt((value.x * value.x) + (value.y * value.y) + (value.z * value.z));
            if (magnitude <= 0.0)
                throw new InvalidOperationException("Spatial ray direction must remain non-zero at the Meta depth boundary.");
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
