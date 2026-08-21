using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;

namespace PhraseLayer.Core.Pipeline
{
    public enum ReadEncounterTransition
    {
        Started = 0,
        Continued = 1,
        PendingSwitch = 2,
        Switched = 3,
        RestartedAfterGap = 4,
        IgnoredStaleObservation = 5
    }

    public sealed class ReadEncounterTrackerOptions
    {
        public ReadEncounterTrackerOptions()
        {
            MaxGapMicroseconds = 2_500_000;
            SwitchConfirmationObservations = 2;
            StrongTextSimilarity = 0.92;
            WeakTextSimilarity = 0.72;
            StrongTextMaxCenterDistance = 0.45;
            WeakTextMaxCenterDistance = 0.18;
        }

        public long MaxGapMicroseconds { get; set; }
        public int SwitchConfirmationObservations { get; set; }
        public double StrongTextSimilarity { get; set; }
        public double WeakTextSimilarity { get; set; }
        public double StrongTextMaxCenterDistance { get; set; }
        public double WeakTextMaxCenterDistance { get; set; }

        internal void Validate()
        {
            if (MaxGapMicroseconds <= 0) throw new ArgumentOutOfRangeException(nameof(MaxGapMicroseconds));
            if (SwitchConfirmationObservations < 1) throw new ArgumentOutOfRangeException(nameof(SwitchConfirmationObservations));
            ValidateUnitInterval(StrongTextSimilarity, nameof(StrongTextSimilarity));
            ValidateUnitInterval(WeakTextSimilarity, nameof(WeakTextSimilarity));
            ValidateUnitInterval(StrongTextMaxCenterDistance, nameof(StrongTextMaxCenterDistance));
            ValidateUnitInterval(WeakTextMaxCenterDistance, nameof(WeakTextMaxCenterDistance));
            if (WeakTextSimilarity > StrongTextSimilarity)
                throw new ArgumentException("Weak text similarity cannot exceed strong text similarity.");
            if (WeakTextMaxCenterDistance > StrongTextMaxCenterDistance)
                throw new ArgumentException("Weak-text spatial tolerance cannot exceed strong-text spatial tolerance.");
        }

        private static void ValidateUnitInterval(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(name);
        }
    }

    public sealed class ReadEncounterDecision
    {
        internal ReadEncounterDecision(
            string encounterId,
            string? previousEncounterId,
            ReadEncounterTransition transition,
            double textSimilarity,
            double? spatialCenterDistance,
            int pendingSwitchObservations)
        {
            EncounterId = encounterId ?? throw new ArgumentNullException(nameof(encounterId));
            PreviousEncounterId = previousEncounterId;
            Transition = transition;
            TextSimilarity = textSimilarity;
            SpatialCenterDistance = spatialCenterDistance;
            PendingSwitchObservations = pendingSwitchObservations;
        }

        public string EncounterId { get; }
        public string? PreviousEncounterId { get; }
        public ReadEncounterTransition Transition { get; }
        public double TextSimilarity { get; }
        public double? SpatialCenterDistance { get; }
        public int PendingSwitchObservations { get; }

        public bool IsNewEncounter =>
            Transition == ReadEncounterTransition.Started ||
            Transition == ReadEncounterTransition.Switched ||
            Transition == ReadEncounterTransition.RestartedAfterGap;

        public bool IsPendingSwitch => Transition == ReadEncounterTransition.PendingSwitch;
        public bool IsStale => Transition == ReadEncounterTransition.IgnoredStaleObservation;
    }

    /// <summary>
    /// Viewport/text hysteresis for the pre-anchor Read MVP.
    ///
    /// This is deliberately a temporary identity layer, not a claim of real-world object identity. It keeps a
    /// frozen language plan stable across small OCR/layout changes and requires repeated contradictory evidence
    /// before switching encounters. Production identity can later replace this with world-space tracking while
    /// preserving the same encounter contract.
    /// </summary>
    public sealed class ReadEncounterTracker
    {
        private readonly ReadEncounterTrackerOptions options;
        private int nextEncounterNumber;
        private string? currentEncounterId;
        private EncounterFingerprint? currentFingerprint;
        private long currentTimestampMicroseconds;
        private EncounterFingerprint? pendingFingerprint;
        private int pendingCount;

        public ReadEncounterTracker(ReadEncounterTrackerOptions? options = null)
        {
            this.options = options ?? new ReadEncounterTrackerOptions();
            this.options.Validate();
        }

        public string? CurrentEncounterId => currentEncounterId;

        public ReadEncounterDecision Observe(
            OcrObservation observation,
            IReadOnlyList<OcrViewportRegion> viewportRegions,
            long timestampMicroseconds)
        {
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            if (viewportRegions == null) throw new ArgumentNullException(nameof(viewportRegions));

            var observed = EncounterFingerprint.Create(observation.Text, viewportRegions);
            var encounterId = currentEncounterId;
            var fingerprint = currentFingerprint;
            if (encounterId == null || !fingerprint.HasValue)
                return StartNew(observed, timestampMicroseconds, ReadEncounterTransition.Started, null, 1.0, null);

            var current = fingerprint.Value;
            if (timestampMicroseconds < currentTimestampMicroseconds)
            {
                var staleComparison = Compare(current, observed);
                return new ReadEncounterDecision(
                    encounterId,
                    null,
                    ReadEncounterTransition.IgnoredStaleObservation,
                    staleComparison.TextSimilarity,
                    staleComparison.SpatialCenterDistance,
                    pendingCount);
            }

            if (timestampMicroseconds - currentTimestampMicroseconds > options.MaxGapMicroseconds)
            {
                var gapComparison = Compare(current, observed);
                return StartNew(
                    observed,
                    timestampMicroseconds,
                    ReadEncounterTransition.RestartedAfterGap,
                    encounterId,
                    gapComparison.TextSimilarity,
                    gapComparison.SpatialCenterDistance);
            }

            var comparison = Compare(current, observed);
            if (comparison.IsMatch)
            {
                currentFingerprint = observed;
                currentTimestampMicroseconds = timestampMicroseconds;
                ClearPending();
                return new ReadEncounterDecision(
                    encounterId,
                    null,
                    ReadEncounterTransition.Continued,
                    comparison.TextSimilarity,
                    comparison.SpatialCenterDistance,
                    0);
            }

            if (!pendingFingerprint.HasValue)
            {
                pendingFingerprint = observed;
                pendingCount = 1;
            }
            else
            {
                var pendingComparison = Compare(pendingFingerprint.Value, observed);
                if (pendingComparison.IsMatch)
                {
                    pendingFingerprint = observed;
                    pendingCount++;
                }
                else
                {
                    pendingFingerprint = observed;
                    pendingCount = 1;
                }
            }

            if (pendingCount >= options.SwitchConfirmationObservations)
            {
                return StartNew(
                    observed,
                    timestampMicroseconds,
                    ReadEncounterTransition.Switched,
                    encounterId,
                    comparison.TextSimilarity,
                    comparison.SpatialCenterDistance);
            }

            currentTimestampMicroseconds = timestampMicroseconds;
            return new ReadEncounterDecision(
                encounterId,
                null,
                ReadEncounterTransition.PendingSwitch,
                comparison.TextSimilarity,
                comparison.SpatialCenterDistance,
                pendingCount);
        }

        public void Reset()
        {
            currentEncounterId = null;
            currentFingerprint = null;
            currentTimestampMicroseconds = 0;
            ClearPending();
        }

        private ReadEncounterDecision StartNew(
            EncounterFingerprint fingerprint,
            long timestampMicroseconds,
            ReadEncounterTransition transition,
            string? previousEncounterId,
            double textSimilarity,
            double? spatialCenterDistance)
        {
            nextEncounterNumber++;
            var encounterId = "read-" + nextEncounterNumber.ToString("D6", CultureInfo.InvariantCulture);
            currentEncounterId = encounterId;
            currentFingerprint = fingerprint;
            currentTimestampMicroseconds = timestampMicroseconds;
            ClearPending();
            return new ReadEncounterDecision(
                encounterId,
                previousEncounterId,
                transition,
                textSimilarity,
                spatialCenterDistance,
                0);
        }

        private EncounterComparison Compare(EncounterFingerprint left, EncounterFingerprint right)
        {
            var textSimilarity = NormalizedSimilarity(left.NormalizedText, right.NormalizedText);
            double? centerDistance = null;
            if (left.Envelope.HasValue && right.Envelope.HasValue)
                centerDistance = Distance(left.Envelope.Value.Center, right.Envelope.Value.Center);

            var isMatch = false;
            if (textSimilarity >= options.StrongTextSimilarity)
            {
                isMatch = !centerDistance.HasValue || centerDistance.Value <= options.StrongTextMaxCenterDistance;
            }
            else if (textSimilarity >= options.WeakTextSimilarity && centerDistance.HasValue)
            {
                isMatch = centerDistance.Value <= options.WeakTextMaxCenterDistance;
            }

            return new EncounterComparison(textSimilarity, centerDistance, isMatch);
        }

        private void ClearPending()
        {
            pendingFingerprint = null;
            pendingCount = 0;
        }

        private static double Distance(ViewportPoint left, ViewportPoint right)
        {
            var du = left.U - right.U;
            var dv = left.V - right.V;
            return Math.Sqrt((du * du) + (dv * dv));
        }

        private static double NormalizedSimilarity(string left, string right)
        {
            if (string.Equals(left, right, StringComparison.Ordinal)) return 1.0;
            if (left.Length == 0 || right.Length == 0) return 0.0;

            var distance = LevenshteinDistance(left, right);
            return Math.Max(0.0, 1.0 - ((double)distance / Math.Max(left.Length, right.Length)));
        }

        private static int LevenshteinDistance(string left, string right)
        {
            if (left.Length > right.Length)
            {
                var swap = left;
                left = right;
                right = swap;
            }

            var previous = new int[left.Length + 1];
            var current = new int[left.Length + 1];
            for (var i = 0; i <= left.Length; i++) previous[i] = i;

            for (var r = 1; r <= right.Length; r++)
            {
                current[0] = r;
                for (var l = 1; l <= left.Length; l++)
                {
                    var substitution = previous[l - 1] + (left[l - 1] == right[r - 1] ? 0 : 1);
                    var insertion = current[l - 1] + 1;
                    var deletion = previous[l] + 1;
                    current[l] = Math.Min(substitution, Math.Min(insertion, deletion));
                }

                var buffer = previous;
                previous = current;
                current = buffer;
            }

            return previous[left.Length];
        }

        private readonly struct EncounterComparison
        {
            public EncounterComparison(double textSimilarity, double? spatialCenterDistance, bool isMatch)
            {
                TextSimilarity = textSimilarity;
                SpatialCenterDistance = spatialCenterDistance;
                IsMatch = isMatch;
            }

            public double TextSimilarity { get; }
            public double? SpatialCenterDistance { get; }
            public bool IsMatch { get; }
        }

        private readonly struct EncounterFingerprint
        {
            public EncounterFingerprint(string normalizedText, EncounterEnvelope? envelope)
            {
                NormalizedText = normalizedText;
                Envelope = envelope;
            }

            public string NormalizedText { get; }
            public EncounterEnvelope? Envelope { get; }

            public static EncounterFingerprint Create(string text, IReadOnlyList<OcrViewportRegion> regions)
            {
                return new EncounterFingerprint(Normalize(text), EncounterEnvelope.TryCreate(regions));
            }

            private static string Normalize(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return string.Empty;
                var builder = new StringBuilder(text.Length);
                var separatorPending = false;
                foreach (var character in text)
                {
                    if (char.IsLetterOrDigit(character))
                    {
                        if (separatorPending && builder.Length > 0) builder.Append(' ');
                        builder.Append(char.ToLowerInvariant(character));
                        separatorPending = false;
                    }
                    else if (builder.Length > 0)
                    {
                        separatorPending = true;
                    }
                }
                return builder.ToString();
            }
        }

        private readonly struct EncounterEnvelope
        {
            public EncounterEnvelope(double minU, double minV, double maxU, double maxV)
            {
                MinU = minU;
                MinV = minV;
                MaxU = maxU;
                MaxV = maxV;
            }

            public double MinU { get; }
            public double MinV { get; }
            public double MaxU { get; }
            public double MaxV { get; }
            public ViewportPoint Center => new ViewportPoint((MinU + MaxU) / 2.0, (MinV + MaxV) / 2.0);

            public static EncounterEnvelope? TryCreate(IReadOnlyList<OcrViewportRegion> regions)
            {
                if (regions.Count == 0) return null;

                var minU = double.PositiveInfinity;
                var minV = double.PositiveInfinity;
                var maxU = double.NegativeInfinity;
                var maxV = double.NegativeInfinity;
                for (var index = 0; index < regions.Count; index++)
                {
                    var points = regions[index].ViewportBounds.Points;
                    for (var pointIndex = 0; pointIndex < points.Count; pointIndex++)
                    {
                        var point = points[pointIndex];
                        minU = Math.Min(minU, point.U);
                        minV = Math.Min(minV, point.V);
                        maxU = Math.Max(maxU, point.U);
                        maxV = Math.Max(maxV, point.V);
                    }
                }

                return new EncounterEnvelope(minU, minV, maxU, maxV);
            }
        }
    }

    public sealed class ReadEncounterResult
    {
        public ReadEncounterResult(ReadEncounterDecision decision, ReadModeSpatialResult spatialResult)
        {
            Decision = decision ?? throw new ArgumentNullException(nameof(decision));
            SpatialResult = spatialResult ?? throw new ArgumentNullException(nameof(spatialResult));
        }

        public ReadEncounterDecision Decision { get; }
        public ReadModeSpatialResult SpatialResult { get; }
    }

    /// <summary>
    /// Freezes the semantic/translation plan for the lifetime of one Read encounter while allowing OCR geometry
    /// to refresh every frame. Learner evidence can therefore affect only a later encounter, never the currently
    /// visible language mix.
    /// </summary>
    public sealed class ReadEncounterPipeline
    {
        private readonly LanguagePipeline language;
        private readonly ReadEncounterTracker tracker;
        private MixedLanguagePlan? frozenPlan;
        private string? frozenEncounterId;

        public ReadEncounterPipeline(LanguagePipeline language, ReadEncounterTracker? tracker = null)
        {
            this.language = language ?? throw new ArgumentNullException(nameof(language));
            this.tracker = tracker ?? new ReadEncounterTracker();
        }

        public async Task<ReadEncounterResult> ProcessAsync(
            ImageFrame frame,
            OcrObservation observation,
            AssistancePolicy policy,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            if (policy == null) throw new ArgumentNullException(nameof(policy));

            cancellationToken.ThrowIfCancellationRequested();
            var viewportRegions = OcrViewportMapper.Map(observation, frame);
            var decision = tracker.Observe(observation, viewportRegions, frame.TimestampMicroseconds);
            var plan = frozenPlan;

            if (decision.IsNewEncounter || plan == null ||
                !string.Equals(frozenEncounterId, decision.EncounterId, StringComparison.Ordinal))
            {
                plan = await language
                    .PlanAsync(observation.Text, policy, observation.Text, cancellationToken)
                    .ConfigureAwait(false);
                frozenPlan = plan;
                frozenEncounterId = decision.EncounterId;
            }

            if (plan == null)
                throw new InvalidOperationException("Read encounter pipeline did not establish a frozen language plan.");

            var spatial = new ReadModeSpatialResult(frame, observation, viewportRegions, plan);
            return new ReadEncounterResult(decision, spatial);
        }

        public void Reset()
        {
            tracker.Reset();
            frozenPlan = null;
            frozenEncounterId = null;
        }
    }
}
