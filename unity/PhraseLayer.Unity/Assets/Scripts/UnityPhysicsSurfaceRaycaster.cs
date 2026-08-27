using System;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Unity-physics implementation of the Core surface projection boundary.
    ///
    /// PhraseLayer deliberately does not invent a depth when no real surface is available. This adapter succeeds
    /// only when a collider-backed surface is hit (for example MRUK/scene geometry or explicit test geometry); the
    /// caller can then keep the existing viewport overlay as a fallback when no hit is available.
    /// </summary>
    public sealed class UnityPhysicsSurfaceRaycaster : ISurfaceRaycaster
    {
        private readonly float maxDistanceMeters;
        private readonly int layerMask;

        public UnityPhysicsSurfaceRaycaster(float maxDistanceMeters = 10f, int layerMask = -1)
        {
            if (float.IsNaN(maxDistanceMeters) || float.IsInfinity(maxDistanceMeters) || maxDistanceMeters <= 0f)
                throw new ArgumentOutOfRangeException(nameof(maxDistanceMeters));

            this.maxDistanceMeters = maxDistanceMeters;
            this.layerMask = layerMask;
        }

        public bool TryRaycast(SpatialRay ray, out SurfaceHit hit)
        {
            var direction = Normalize(ToUnity(ray.Direction));
            var unityRay = new Ray(ToUnity(ray.Origin), direction);
            RaycastHit unityHit;
            if (!Physics.Raycast(
                    unityRay,
                    out unityHit,
                    maxDistanceMeters,
                    layerMask,
                    QueryTriggerInteraction.Ignore))
            {
                hit = default(SurfaceHit);
                return false;
            }

            hit = new SurfaceHit(
                ToSpatial(unityHit.point),
                ToSpatial(unityHit.normal),
                unityHit.distance);
            return true;
        }

        private static Vector3 Normalize(Vector3 value)
        {
            var magnitude = Math.Sqrt((value.x * value.x) + (value.y * value.y) + (value.z * value.z));
            if (magnitude <= 0.0)
                throw new InvalidOperationException("Spatial ray direction must remain non-zero at the Unity boundary.");
            return new Vector3(
                (float)(value.x / magnitude),
                (float)(value.y / magnitude),
                (float)(value.z / magnitude));
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
