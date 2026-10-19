using System;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Owns temporal world-text stabilization and presentation. A source mask is permitted only after the
    /// replacement renderer accepts the same plan. Errors, disablement and receipt-clock expiry remove covers.
    /// Receipt age is not camera exposure age: these clocks are deliberately never subtracted from each other.
    /// </summary>
    public sealed class UnityWorldTextTrackingBehaviour : MonoBehaviour
    {
        [SerializeField] private UnitySpatialProjectionBehaviour projection = default(UnitySpatialProjectionBehaviour);
        [SerializeField] private UnityWorldTextRendererBehaviour renderer = default(UnityWorldTextRendererBehaviour);
        [SerializeField] private UnityWorldTextSourceMaskBehaviour sourceMask = default(UnityWorldTextSourceMaskBehaviour);
        [SerializeField] private float maximumAssociationDistanceMeters = 0.15f;
        [SerializeField] private float retentionSeconds = 0.60f;
        [SerializeField] private float smoothingTimeConstantSeconds = 0.12f;

        private WorldTextTrackStabilizer stabilizer;
        private double lastLayoutReceiptSeconds = double.NaN;

        public WorldTextTrackingPlan LastPlan { get; private set; }
        public UnitySpatialProjectionBehaviour Projection => projection;
        public UnityWorldTextRendererBehaviour Renderer => renderer;
        public UnityWorldTextSourceMaskBehaviour SourceMask => sourceMask;
        public bool LastRenderSucceeded { get; private set; }
        public bool LastMaskSucceeded { get; private set; }

        public void SetProjection(UnitySpatialProjectionBehaviour spatialProjection)
        {
            if (spatialProjection == null) throw new ArgumentNullException(nameof(spatialProjection));
            ResetTracking();
            projection = spatialProjection;
        }

        public void SetRenderer(UnityWorldTextRendererBehaviour worldTextRenderer)
        {
            if (worldTextRenderer == null) throw new ArgumentNullException(nameof(worldTextRenderer));
            // Release the old presenter before losing the reference to it.
            ResetTracking();
            renderer = worldTextRenderer;
        }

        public void SetSourceMask(UnityWorldTextSourceMaskBehaviour worldTextSourceMask)
        {
            if (worldTextSourceMask == null) throw new ArgumentNullException(nameof(worldTextSourceMask));
            ResetTracking();
            sourceMask = worldTextSourceMask;
        }

        public WorldTextTrackingPlan ProjectFitAndTrack(ReadModeAlignedResult aligned, long timestampMicroseconds)
        {
            try
            {
                if (aligned == null) throw new ArgumentNullException(nameof(aligned));
                EnsureStabilizer();
                if (projection == null)
                    throw new InvalidOperationException("Assign UnitySpatialProjectionBehaviour before tracking world text.");
                return Track(projection.ProjectAndFitWorldText(aligned), timestampMicroseconds);
            }
            catch
            {
                ResetTracking();
                throw;
            }
        }

        public WorldTextTrackingPlan Track(WorldTextLayoutPlan layout, long timestampMicroseconds)
        {
            try
            {
                if (layout == null) throw new ArgumentNullException(nameof(layout));
                EnsureStabilizer();
                var receiptSeconds = Time.realtimeSinceStartupAsDouble;
                if (double.IsNaN(receiptSeconds) || double.IsInfinity(receiptSeconds) || receiptSeconds < 0.0)
                    throw new InvalidOperationException("World-text receipt clock must be finite and non-negative.");
                LastPlan = stabilizer.Update(layout, timestampMicroseconds);
                lastLayoutReceiptSeconds = receiptSeconds;
                PresentIfConfigured();
                return LastPlan;
            }
            catch
            {
                ResetTracking();
                throw;
            }
        }

        public void ResetTracking()
        {
            stabilizer = null;
            LastPlan = null;
            lastLayoutReceiptSeconds = double.NaN;
            LastRenderSucceeded = false;
            LastMaskSucceeded = false;
            try { sourceMask?.Clear(); }
            finally { renderer?.Clear(); }
        }

        private void OnDisable() { ResetTracking(); }
        private void OnDestroy() { ResetTracking(); }

        private void OnValidate()
        {
            ResetTracking();
            ValidateConfiguration();
        }

        private void LateUpdate()
        {
            if (LastPlan == null) return;
            // Runs even when camera/OCR/translation stops producing observations. Do not age using camera timestamps.
            var ageSeconds = Time.realtimeSinceStartupAsDouble - lastLayoutReceiptSeconds;
            if (double.IsNaN(ageSeconds) || double.IsInfinity(ageSeconds) || ageSeconds < 0.0 || ageSeconds >= retentionSeconds)
                ResetTracking();
        }

        private void PresentIfConfigured()
        {
            LastRenderSucceeded = false;
            LastMaskSucceeded = false;
            // Configure text first; both presenter updates happen before Unity renders this frame. A failed or absent
            // renderer must never leave the physical source hidden. Mask failure may still leave non-destructive text.
            LastRenderSucceeded = renderer != null && renderer.TryPresent(LastPlan);
            if (!LastRenderSucceeded)
            {
                sourceMask?.Clear();
                renderer?.Clear();
                return;
            }
            LastMaskSucceeded = sourceMask != null && sourceMask.TryPresent(LastPlan);
            if (!LastMaskSucceeded) sourceMask?.Clear();
        }

        private void EnsureStabilizer()
        {
            if (stabilizer != null) return;
            ValidateConfiguration();
            stabilizer = new WorldTextTrackStabilizer(maximumAssociationDistanceMeters, retentionSeconds, smoothingTimeConstantSeconds);
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
        private static bool IsFinitePositive(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
    }
}
