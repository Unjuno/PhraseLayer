using System;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Quest Read surface strategy: prefer Meta Environment Depth when Spatial Data permission and device support are
    /// available, then fall back to ordinary Unity collider geometry. Neither path invents a depth value.
    /// </summary>
    public sealed class QuestSurfaceRaycaster : ISurfaceRaycaster
    {
        private readonly MetaEnvironmentDepthSurfaceRaycaster environmentDepth;
        private readonly UnityPhysicsSurfaceRaycaster physics;

        public QuestSurfaceRaycaster(
            GameObject owner,
            float maxDistanceMeters = 10f,
            int physicsLayerMask = -1)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            environmentDepth = new MetaEnvironmentDepthSurfaceRaycaster(owner, maxDistanceMeters);
            physics = new UnityPhysicsSurfaceRaycaster(maxDistanceMeters, physicsLayerMask);
        }

        public bool HasEnvironmentDepthApi => environmentDepth.IsApiAvailable;

        public bool TryRaycast(SpatialRay ray, out SurfaceHit hit)
        {
            if (environmentDepth.TryRaycast(ray, out hit))
                return true;
            return physics.TryRaycast(ray, out hit);
        }
    }
}
