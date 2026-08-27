using System;
using System.Collections.Generic;
using PhraseLayer.Core.Inputs;

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
            MaxMissingObservations = 2;
        }

        /// <summary>
        /// Fraction of the newest observation applied for small movements. 1.0 disables smoothing.
        /// </summary>
        public double BlendFactor { get; set; }

        /// <summary>
        /// Viewport-center displacement above which the observed envelope is accepted immediately.
        /// </summary>
        public double ResetCenterDistance { get; set; }

        /// <summary>
        /// Number of consecutive missing observations for which a previously placeable semantic target may keep
        /// its last stabilized envelope. Zero disables dropout retention.
        /// </summary>
        public int MaxMissingObservations { get; set; }

        internal void Validate()
        {
            if (double.IsNaN(BlendFactor) || double.IsInfinity(BlendFactor) || BlendFactor <= 0.0 || BlendFactor > 1.0)
                throw new ArgumentOutOfRangeException(nameof(BlendFactor));
            if (double.IsNaN(ResetCenterDistance) || double.IsInfinity(ResetCenterDistance) || ResetCenterDistance < 0.0)
                throw new ArgumentOutOfRangeException(nameof(ResetCenterDistance));
            if (MaxMissingObservations < 0)
                throw new ArgumentOutOfRangeException(nameof(MaxMissingObservations));
        }
    }

    /// <summary>
    /// Per-semantic-target temporal stabilization for OCR-derived viewport envelopes.
    ///
    /// This is intentionally viewport-only stabilization for the pre-anchor Read MVP. It does not claim world-space
    /// persistence. Small box jitter is smoothed, short OCR dropouts can retain the last placeable envelope, and large
    /// motion is accepted immediately. Call Reset when a Read encounter changes so geometry from one real-world text
    /// surface can never leak into another encounter.
    /// </summary>
    public sealed class ViewportEnvelopeStabilizer
    {
        private sealed class EnvelopeState
        {
            public EnvelopeState(ViewportEnvelope envelope)
            {
                Envelope = envelope;
                MissingObservations = 0;
            }

            public ViewportEnvelope Envelope { get; set; }
            public int MissingObservations { get; set; }
        }

        private readonly ViewportEnvelopeStabilizerOptions options;
        private readonly Dictionary<string, EnvelopeState> states =
            new Dictionary<string, EnvelopeState>(StringComparer.Ordinal);

        public ViewportEnvelopeStabilizer(ViewportEnvelopeStabilizerOptions? options = null)
        {
            this.options = options ?? new ViewportEnvelopeStabilizerOptions();
            this.options.Validate();
        }

        public int Count => states.Count;

        public ViewportEnvelope Stabilize(string key, ViewportEnvelope observed)
        {
            ValidateKey(key);

            if (!states.TryGetValue(key, out var state))
            {
                states[key] = new EnvelopeState(observed);
                return observed;
            }

            var centerDistance = Distance(state.Envelope.Center, observed.Center);
            var stabilized = centerDistance > options.ResetCenterDistance
                ? observed
                : Blend(state.Envelope, observed, options.BlendFactor);
            state.Envelope = stabilized;
            state.MissingObservations = 0;
            return stabilized;
        }

        /// <summary>
        /// Returns the last stabilized envelope for a bounded number of consecutive missing observations.
        /// Once the budget is exhausted the state is removed, preventing stale geometry from persisting indefinitely.
        /// </summary>
        public bool TryHoldMissing(string key, out ViewportEnvelope envelope)
        {
            ValidateKey(key);

            if (!states.TryGetValue(key, out var state) || options.MaxMissingObservations == 0)
            {
                envelope = default(ViewportEnvelope);
                return false;
            }

            state.MissingObservations++;
            if (state.MissingObservations > options.MaxMissingObservations)
            {
                states.Remove(key);
                envelope = default(ViewportEnvelope);
                return false;
            }

            envelope = state.Envelope;
            return true;
        }

        public bool TryGet(string key, out ViewportEnvelope envelope)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (states.TryGetValue(key, out var state))
            {
                envelope = state.Envelope;
                return true;
            }

            envelope = default(ViewportEnvelope);
            return false;
        }

        public void Reset()
        {
            states.Clear();
        }

        private static void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A stable semantic target key is required.", nameof(key));
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
