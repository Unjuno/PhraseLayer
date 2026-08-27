using System;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Unity-facing owner for temporal world-text stabilization. It consumes only layout-ready surfaces produced by
    /// UnitySpatialProjectionBehaviour and keeps tracking policy in platform-neutral Core.
    /// </summary>
    public sealed class UnityWorldTextTrackingBehaviour : MonoBehaviour
    {
        [SerializeField] private UnitySpatialProjectionBehaviour projection = default(UnitySpatialProjectionBehaviour);
        [SerializeField] private UnityWorldTextRendererBehaviour renderer = default(UnityWorldTextRendererBehaviour);
        [SerializeField] private float maximumAssociationDistanceMeters = 0.15f;
        [SerializeField] private float retentionSeconds = 0.60f;
        [SerializeField] private float smoothingTimeConstantSeconds = 0.12f;

        private WorldTextTrackStabilizer stabilizer;

        public WorldTextTrackingPlan LastPlan { get; private set; }
        public UnitySpatialProjectionBehaviour Projection => projection;
        public UnityWorldTextRendererBehaviour Renderer => renderer;
        public bool LastRenderSucceeded { get; private set; }

        public void SetProjection(UnitySpatialProjectionBehaviour spatialProjection)
        {
            projection = spatialProjection ?? throw new ArgumentNullException(nameof(spatialProjection));
            ResetTracking();
        }

        public void SetRenderer(UnityWorldTextRendererBehaviour worldTextRenderer)
        {
            renderer = worldTextRenderer ?? throw new ArgumentNullException(nameof(worldTextRenderer));
            LastRenderSucceeded = false;
        }

        public WorldTextTrackingPlan ProjectFitAndTrack(
            ReadModeAlignedResult aligned,
            long timestampMicroseconds)
        {
            if (aligned == null) throw new ArgumentNullException(nameof(aligned));
            EnsureStabilizer();
            if (projection == null)
                throw new InvalidOperationException("Assign UnitySpatialProjectionBehaviour before tracking world text.");

            var layout = projection.ProjectAndFitWorldText(aligned);
            LastPlan = stabilizer.Update(layout, timestampMicroseconds);
            PresentIfConfigured();
            return LastPlan;
        }

        public WorldTextTrackingPlan Track(
            WorldTextLayoutPlan layout,
            long timestampMicroseconds)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            EnsureStabilizer();
            LastPlan = stabilizer.Update(layout, timestampMicroseconds);
            PresentIfConfigured();
            return LastPlan;
        }

        public void ResetTracking()
        {
            stabilizer = null;
            LastPlan = null;
            LastRenderSucceeded = false;
            renderer?.Clear();
        }

        private void OnValidate()
        {
            ValidateConfiguration();
            stabilizer = null;
            LastPlan = null;
            LastRenderSucceeded = false;
        }

        private void PresentIfConfigured()
        {
            LastRenderSucceeded = renderer != null && renderer.TryPresent(LastPlan);
        }

        private void EnsureStabilizer()
        {
            if (stabilizer != null) return;
            ValidateConfiguration();
            stabilizer = new WorldTextTrackStabilizer(
                maximumAssociationDistanceMeters,
                retentionSeconds,
                smoothingTimeConstantSeconds);
        }

        private void ValidateConfiguration()
        {
            if (!IsFinitePositive(maximumAssociationDistanceMeters))
                throw new InvalidOperationException("World text association distance must be finite and greater than zero meters.");
            if (!IsFinitePositive(retentionSeconds))
                throw new InvalidOperationException("World text retention must be finite and greater than zero seconds.");
            if (!IsFinitePositive(smoothingTimeConstantSeconds))
                throw new InvalidOperationException("World text smoothing time constant must be finite and greater than zero seconds.");
        }

        private static bool IsFinitePositive(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }
    }
}
