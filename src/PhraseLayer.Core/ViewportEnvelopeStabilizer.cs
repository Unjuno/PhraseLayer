using System;
using System.Collections.Generic;

namespace PhraseLayer.Core.Spatial
{
    /// <summary>
    /// Configuration for conservative viewport-space overlay stabilization.
    /// Small frame-to-frame OCR box jitter is blended, while a larger displacement is treated as real motion and
    /// accepted immediately so head movement does not leave a visibly lagging overlay behind.
    /// </summary>
    public sealed class ViewportEnvelopeStabilizerOptions
    {
        public ViewportEnvelopeStabilizerOptions()
        {
            BlendFactor = 0.35;
            ResetCenterDistance = 0.10;
        }

        /// <summary>
        /// Fraction of the newest observation applied for small movements. 1.0 disables smoothing.
        /// </summary>
        public double BlendFactor { get; set; }

        /// <summary>
        /// Viewport-center displacement above which the observed envelope is accepted immediately.
        /// </summary>
        public double ResetCenterDistance { get; set; }

        internal void Validate()
        {
            if (double.IsNaN(BlendFactor) || double.IsInfinity(BlendFactor) || BlendFactor <= 0.0 || BlendFactor > 1.0)
                throw new ArgumentOutOfRangeException(nameof(BlendFactor));
            if (double.IsNaN(ResetCenterDistance) || double.IsInfinity(ResetCenterDistance) || ResetCenterDistance < 0.0)
                throw new ArgumentOutOfRangeException(nameof(ResetCenterDistance));
        }
    }

    /// <summary>
    /// Per-semantic-target exponential smoothing for OCR-derived viewport envelopes.
    ///
    /// This is intentionally viewport-only stabilization for the pre-anchor Read MVP. It does not claim world-space
    /// persistence. Call Reset when a Read encounter changes so geometry from one real-world text surface can never
    /// leak into another encounter.
    /// </summary>
    public sealed class ViewportEnvelopeStabilizer
    {
        private readonly ViewportEnvelopeStabilizerOptions options;
        private readonly Dictionary<string, ViewportEnvelope> states =
            new Dictionary<string, ViewportEnvelope>(StringComparer.Ordinal);

        public ViewportEnvelopeStabilizer(ViewportEnvelopeStabilizerOptions? options = null)
        {
            this.options = options ?? new ViewportEnvelopeStabilizerOptions();
            this.options.Validate();
        }

        public int Count => states.Count;

        public ViewportEnvelope Stabilize(string key, ViewportEnvelope observed)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A stable semantic target key is required.", nameof(key));

            if (!states.TryGetValue(key, out var previous))
            {
                states[key] = observed;
                return observed;
            }

            var centerDistance = Distance(previous.Center, observed.Center);
            var stabilized = centerDistance > options.ResetCenterDistance
                ? observed
                : Blend(previous, observed, options.BlendFactor);
            states[key] = stabilized;
            return stabilized;
        }

        public bool TryGet(string key, out ViewportEnvelope envelope)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            return states.TryGetValue(key, out envelope);
        }

        public void Reset()
        {
            states.Clear();
        }

        private static ViewportEnvelope Blend(ViewportEnvelope previous, ViewportEnvelope observed, double factor)
        {
            return new ViewportEnvelope(
                Lerp(previous.MinU, observed.MinU, factor),
                Lerp(previous.MinV, observed.MinV, factor),
                Lerp(previous.MaxU, observed.MaxU, factor),
                Lerp(previous.MaxV, observed.MaxV, factor));
        }

        private static double Lerp(double from, double to, double factor)
        {
            return from + ((to - from) * factor);
        }

        private static double Distance(ViewportPoint left, ViewportPoint right)
        {
            var du = left.U - right.U;
            var dv = left.V - right.V;
            return Math.Sqrt((du * du) + (dv * dv));
        }
    }
}
