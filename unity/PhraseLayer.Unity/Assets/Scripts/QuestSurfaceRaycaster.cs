using System;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Quest Read surface strategy: prefer Meta's permission-gated native environment raycaster when available,
    /// then fall back to ordinary Unity collider geometry. Neither path invents a depth value.
    /// </summary>
    public sealed class QuestSurfaceRaycaster : ISurfaceRaycaster, IDisposable
    {
        private readonly MetaEnvironmentDepthSurfaceRaycaster environmentDepth;
        private readonly UnityPhysicsSurfaceRaycaster physics;
        private bool disposed;

        public QuestSurfaceRaycaster(
            GameObject owner,
            float maxDistanceMeters = 10f,
            int physicsLayerMask = -1)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            environmentDepth = new MetaEnvironmentDepthSurfaceRaycaster(owner, maxDistanceMeters);
            physics = new UnityPhysicsSurfaceRaycaster(maxDistanceMeters, physicsLayerMask);
        }

        public bool HasEnvironmentDepthApi => !disposed && environmentDepth.IsApiAvailable;

        public bool TryRaycast(SpatialRay ray, out SurfaceHit hit)
        {
            if (disposed)
            {
                hit = default(SurfaceHit);
                return false;
            }

            if (environmentDepth.TryRaycast(ray, out hit))
                return true;
            return physics.TryRaycast(ray, out hit);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            environmentDepth.Dispose();
        }
    }
}
