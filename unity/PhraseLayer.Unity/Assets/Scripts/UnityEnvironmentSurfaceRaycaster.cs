using System;
using System.Reflection;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// ISurfaceRaycaster adapter over MRUK v85 EnvironmentRaycastManager.
    ///
    /// The Meta type is resolved and its public Raycast ABI is validated at runtime so PhraseLayer's host compile
    /// remains independent of the proprietary SDK assembly. The Quest fixture scene creates the real MRUK component,
    /// and this adapter refuses to guess a surface when environment raycasting is unsupported, not ready, outside
    /// the depth frustum, occluded, or otherwise returns false.
    /// </summary>
    public sealed class UnityEnvironmentSurfaceRaycaster : MonoBehaviour, ISurfaceRaycaster
    {
        public const string ExpectedManagerTypeName = "Meta.XR.EnvironmentRaycastManager";

        [SerializeField] private Component environmentRaycastManager = default(Component);
        [SerializeField] private float maxDistanceMeters = 10f;
        [SerializeField] private float minimumNormalConfidence = 0f;

        private Type managerType;
        private MethodInfo raycastMethod;
        private PropertyInfo isSupportedProperty;
        private Type hitType;
        private MemberInfo hitPointMember;
        private MemberInfo hitNormalMember;
        private MemberInfo hitNormalConfidenceMember;
        private MemberInfo hitStatusMember;

        public Component EnvironmentRaycastManager => environmentRaycastManager;
        public float MaxDistanceMeters => maxDistanceMeters;
        public float MinimumNormalConfidence => minimumNormalConfidence;
        public float? LastNormalConfidence { get; private set; }
        public string LastHitStatus { get; private set; }
        public bool AbiValidated => raycastMethod != null && hitType != null;

        public void SetEnvironmentRaycastManager(Component manager)
        {
            environmentRaycastManager = manager ?? throw new ArgumentNullException(nameof(manager));
            ResetAbi();
            EnsureAbi();
        }

        public bool TryRaycast(SpatialRay ray, out SurfaceHit hit)
        {
            ValidateConfiguration();
            EnsureAbi();
            LastNormalConfidence = null;
            LastHitStatus = null;

            if (!ReadIsSupported())
            {
                hit = default(SurfaceHit);
                LastHitStatus = "NotSupported";
                return false;
            }

            var origin = ToFiniteVector3(ray.Origin, "ray origin");
            var directionMagnitude = Math.Sqrt(ray.Direction.SquaredMagnitude);
            if (double.IsNaN(directionMagnitude) || double.IsInfinity(directionMagnitude) || directionMagnitude <= 0.0)
                throw new ArgumentException("Spatial ray direction must have a finite non-zero magnitude.", nameof(ray));

            var direction = ToFiniteVector3(
                new SpatialVector3(
                    ray.Direction.X / directionMagnitude,
                    ray.Direction.Y / directionMagnitude,
                    ray.Direction.Z / directionMagnitude),
                "normalized ray direction");
            var unityRay = new Ray(origin, direction);
            var arguments = new object[] { unityRay, null, maxDistanceMeters };

            bool succeeded;
            try
            {
                succeeded = (bool)raycastMethod.Invoke(environmentRaycastManager, arguments);
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    "MRUK EnvironmentRaycastManager.Raycast threw while projecting PhraseLayer text.",
                    exception.InnerException ?? exception);
            }

            var boxedHit = arguments[1];
            if (boxedHit != null)
            {
                LastHitStatus = ReadOptionalMember(hitStatusMember, boxedHit)?.ToString();
                var confidenceValue = ReadOptionalMember(hitNormalConfidenceMember, boxedHit);
                if (confidenceValue != null)
                    LastNormalConfidence = Convert.ToSingle(confidenceValue);
            }

            if (!succeeded || boxedHit == null)
            {
                hit = default(SurfaceHit);
                return false;
            }

            if (LastNormalConfidence.HasValue && LastNormalConfidence.Value < minimumNormalConfidence)
            {
                hit = default(SurfaceHit);
                LastHitStatus = "LowNormalConfidence";
                return false;
            }

            var point = RequireVector3(ReadMember(hitPointMember, boxedHit), "EnvironmentRaycastHit.point");
            var normal = RequireVector3(ReadMember(hitNormalMember, boxedHit), "EnvironmentRaycastHit.normal");
            EnsureFinite(point, "environment hit point");
            EnsureFinite(normal, "environment hit normal");

            var dx = (double)point.x - origin.x;
            var dy = (double)point.y - origin.y;
            var dz = (double)point.z - origin.z;
            var distance = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (double.IsNaN(distance) || double.IsInfinity(distance) || distance <= 0.0 || distance > maxDistanceMeters + 0.001)
            {
                hit = default(SurfaceHit);
                LastHitStatus = "InvalidDistance";
                return false;
            }

            hit = new SurfaceHit(ToSpatial(point), ToSpatial(normal), distance);
            return true;
        }

        private void OnValidate()
        {
            ValidateConfiguration();
            ResetAbi();
        }

        private void ValidateConfiguration()
        {
            if (float.IsNaN(maxDistanceMeters) || float.IsInfinity(maxDistanceMeters) || maxDistanceMeters <= 0f)
                throw new InvalidOperationException("Environment raycast max distance must be finite and greater than zero meters.");
            if (float.IsNaN(minimumNormalConfidence) || float.IsInfinity(minimumNormalConfidence) ||
                minimumNormalConfidence < 0f || minimumNormalConfidence > 1f)
            {
                throw new InvalidOperationException("Environment raycast minimum normal confidence must be finite and within [0,1].");
            }
        }

        private void EnsureAbi()
        {
            if (raycastMethod != null) return;
            if (environmentRaycastManager == null)
                throw new InvalidOperationException("Assign MRUK EnvironmentRaycastManager before projecting Read Mode assistance.");

            managerType = environmentRaycastManager.GetType();
            if (!string.Equals(managerType.FullName, ExpectedManagerTypeName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Unexpected MRUK environment raycast component type. Expected " + ExpectedManagerTypeName +
                    " but found " + (managerType.FullName ?? managerType.Name) + ".");
            }

            isSupportedProperty = managerType.GetProperty("IsSupported");
            if (isSupportedProperty == null || isSupportedProperty.PropertyType != typeof(bool) ||
                !isSupportedProperty.GetMethod.IsStatic)
            {
                throw new MissingMemberException(ExpectedManagerTypeName, "static bool IsSupported");
            }

            var methods = managerType.GetMethods();
            for (var index = 0; index < methods.Length; index++)
            {
                var candidate = methods[index];
                if (!string.Equals(candidate.Name, "Raycast", StringComparison.Ordinal) || candidate.ReturnType != typeof(bool))
                    continue;
                var parameters = candidate.GetParameters();
                if (parameters.Length != 3 || parameters[0].ParameterType != typeof(Ray) || parameters[2].ParameterType != typeof(float))
                    continue;
                if (!parameters[1].IsOut || !parameters[1].ParameterType.IsByRef)
                    continue;

                raycastMethod = candidate;
                hitType = parameters[1].ParameterType.GetElementType();
                break;
            }

            if (raycastMethod == null || hitType == null)
                throw new MissingMethodException(ExpectedManagerTypeName, "bool Raycast(Ray, out EnvironmentRaycastHit, float)");

            hitPointMember = FindRequiredMember(hitType, "point");
            hitNormalMember = FindRequiredMember(hitType, "normal");
            hitNormalConfidenceMember = FindOptionalMember(hitType, "normalConfidence");
            hitStatusMember = FindOptionalMember(hitType, "status");
        }

        private bool ReadIsSupported()
        {
            try
            {
                return (bool)isSupportedProperty.GetValue(null);
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    "MRUK EnvironmentRaycastManager.IsSupported threw while checking Quest environment raycasting.",
                    exception.InnerException ?? exception);
            }
        }

        private void ResetAbi()
        {
            managerType = null;
            raycastMethod = null;
            isSupportedProperty = null;
            hitType = null;
            hitPointMember = null;
            hitNormalMember = null;
            hitNormalConfidenceMember = null;
            hitStatusMember = null;
            LastNormalConfidence = null;
            LastHitStatus = null;
        }

        private static MemberInfo FindRequiredMember(Type type, string name)
        {
            var member = FindOptionalMember(type, name);
            if (member == null)
                throw new MissingMemberException(type.FullName, name);
            return member;
        }

        private static MemberInfo FindOptionalMember(Type type, string name)
        {
            var field = type.GetField(name);
            if (field != null) return field;
            var property = type.GetProperty(name);
            return property;
        }

        private static object ReadMember(MemberInfo member, object instance)
        {
            var value = ReadOptionalMember(member, instance);
            if (value == null)
                throw new InvalidOperationException("MRUK environment raycast hit member returned null: " + member.Name);
            return value;
        }

        private static object ReadOptionalMember(MemberInfo member, object instance)
        {
            if (member == null || instance == null) return null;
            var field = member as FieldInfo;
            if (field != null) return field.GetValue(instance);
            var property = member as PropertyInfo;
            return property?.GetValue(instance);
        }

        private static Vector3 RequireVector3(object value, string label)
        {
            if (!(value is Vector3))
                throw new InvalidOperationException(label + " must remain a UnityEngine.Vector3 in the reviewed MRUK API.");
            return (Vector3)value;
        }

        private static Vector3 ToFiniteVector3(SpatialVector3 value, string label)
        {
            return new Vector3(
                ToFiniteFloat(value.X, label + " X"),
                ToFiniteFloat(value.Y, label + " Y"),
                ToFiniteFloat(value.Z, label + " Z"));
        }

        private static float ToFiniteFloat(double value, string label)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < -float.MaxValue || value > float.MaxValue)
                throw new ArgumentOutOfRangeException(label, "Spatial coordinate cannot be represented as a finite Unity float.");
            return (float)value;
        }

        private static void EnsureFinite(Vector3 value, string label)
        {
            if (float.IsNaN(value.x) || float.IsInfinity(value.x) ||
                float.IsNaN(value.y) || float.IsInfinity(value.y) ||
                float.IsNaN(value.z) || float.IsInfinity(value.z))
            {
                throw new InvalidOperationException(label + " must be finite.");
            }
        }

        private static SpatialVector3 ToSpatial(Vector3 value)
        {
            return new SpatialVector3(value.x, value.y, value.z);
        }
    }
}
