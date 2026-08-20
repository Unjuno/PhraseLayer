using System;
using PhraseLayer.Core.Semantics;

namespace PhraseLayer.Core.Learning
{
    public enum LearningEvidenceKind
    {
        AssistedExposure = 0,
        CompletedWithoutAssistance = 1,
        AssistanceRequested = 2,
        RecallSucceeded = 3,
        RecallFailed = 4,
        MarkedKnown = 5,
        MarkedUnknown = 6,
        VerifiedUnaidedSuccess = 7
    }

    /// <summary>
    /// The action/context that made a learner observation possible.
    /// Observation likelihoods are action-dependent: silence under an assisted display is not the
    /// same observation as silence under a source-only display.
    /// </summary>
    public enum LearningObservationOrigin
    {
        Unknown = 0,
        SourceDisplay = 1,
        AssistedDisplay = 2,
        RecallProbe = 3,
        ExplicitSelfReport = 4
    }

    /// <summary>
    /// An action-aware observation about one semantic unit.
    /// Exposure and silent completion may be logged, but they are not treated as mastery evidence.
    /// </summary>
    public sealed class LearningObservation
    {
        public LearningObservation(
            SemanticUnit unit,
            LearningEvidenceKind evidence,
            LearningObservationOrigin origin,
            bool engagementVerified)
        {
            Unit = unit ?? throw new ArgumentNullException(nameof(unit));
            Evidence = evidence;
            Origin = origin;
            EngagementVerified = engagementVerified;
        }

        public SemanticUnit Unit { get; }
        public LearningEvidenceKind Evidence { get; }
        public LearningObservationOrigin Origin { get; }
        public bool EngagementVerified { get; }

        public static LearningObservation ForEvidence(
            SemanticUnit unit,
            LearningEvidenceKind evidence,
            bool? assistedDisplay = null)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));

            switch (evidence)
            {
                case LearningEvidenceKind.AssistedExposure:
                    return new LearningObservation(
                        unit,
                        evidence,
                        LearningObservationOrigin.AssistedDisplay,
                        engagementVerified: false);
                case LearningEvidenceKind.CompletedWithoutAssistance:
                    return new LearningObservation(
                        unit,
                        evidence,
                        LearningObservationOrigin.SourceDisplay,
                        engagementVerified: false);
                case LearningEvidenceKind.AssistanceRequested:
                    if (!assistedDisplay.HasValue)
                    {
                        throw new InvalidOperationException(
                            "AssistanceRequested is action-dependent. The caller must specify whether the unit was assisted when the request occurred.");
                    }
                    return new LearningObservation(
                        unit,
                        evidence,
                        assistedDisplay.Value
                            ? LearningObservationOrigin.AssistedDisplay
                            : LearningObservationOrigin.SourceDisplay,
                        engagementVerified: true);
                case LearningEvidenceKind.RecallSucceeded:
                case LearningEvidenceKind.RecallFailed:
                    return new LearningObservation(
                        unit,
                        evidence,
                        LearningObservationOrigin.RecallProbe,
                        engagementVerified: true);
                case LearningEvidenceKind.MarkedKnown:
                case LearningEvidenceKind.MarkedUnknown:
                    return new LearningObservation(
                        unit,
                        evidence,
                        LearningObservationOrigin.ExplicitSelfReport,
                        engagementVerified: true);
                case LearningEvidenceKind.VerifiedUnaidedSuccess:
                    return new LearningObservation(
                        unit,
                        evidence,
                        LearningObservationOrigin.SourceDisplay,
                        engagementVerified: true);
                default:
                    throw new ArgumentOutOfRangeException(nameof(evidence), evidence, "Unknown learning evidence kind.");
            }
        }
    }

    /// <summary>
    /// Tunable evidence-update policy.
    ///
    /// AssistedExposureGain and UnassistedCompletionGain are retained as compatibility knobs for the
    /// earlier prototype, but the observation updater deliberately does not use them. Passive exposure
    /// belongs in a separately calibrated transition model, and silent completion is not evidence unless
    /// engagement/success has been independently verified.
    /// </summary>
    public sealed class LearnerAdaptationPolicy
    {
        public LearnerAdaptationPolicy(
            double assistedExposureGain = 0.0,
            double assistedExposureCeiling = 0.80,
            double unassistedCompletionGain = 0.0,
            double assistanceRequestLoss = 0.20,
            double recallSuccessGain = 0.20,
            double recallFailureLoss = 0.30,
            double markedKnownTarget = 0.98,
            double markedUnknownTarget = 0.10,
            double verifiedUnaidedSuccessGain = 0.08)
        {
            AssistedExposureGain = Validate01(assistedExposureGain, nameof(assistedExposureGain));
            AssistedExposureCeiling = Validate01(assistedExposureCeiling, nameof(assistedExposureCeiling));
            UnassistedCompletionGain = Validate01(unassistedCompletionGain, nameof(unassistedCompletionGain));
            AssistanceRequestLoss = Validate01(assistanceRequestLoss, nameof(assistanceRequestLoss));
            RecallSuccessGain = Validate01(recallSuccessGain, nameof(recallSuccessGain));
            RecallFailureLoss = Validate01(recallFailureLoss, nameof(recallFailureLoss));
            MarkedKnownTarget = Validate01(markedKnownTarget, nameof(markedKnownTarget));
            MarkedUnknownTarget = Validate01(markedUnknownTarget, nameof(markedUnknownTarget));
            VerifiedUnaidedSuccessGain = Validate01(verifiedUnaidedSuccessGain, nameof(verifiedUnaidedSuccessGain));
        }

        public double AssistedExposureGain { get; }
        public double AssistedExposureCeiling { get; }
        public double UnassistedCompletionGain { get; }
        public double AssistanceRequestLoss { get; }
        public double RecallSuccessGain { get; }
        public double RecallFailureLoss { get; }
        public double MarkedKnownTarget { get; }
        public double MarkedUnknownTarget { get; }
        public double VerifiedUnaidedSuccessGain { get; }

        private static double Validate01(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(parameterName, "Value must be finite and within [0,1].");
            return value;
        }
    }

    public sealed class LearnerUpdate
    {
        public LearnerUpdate(
            SemanticUnit unit,
            LearningEvidenceKind evidence,
            double previousUnderstanding,
            double updatedUnderstanding,
            LearningObservationOrigin origin = LearningObservationOrigin.Unknown,
            bool engagementVerified = false,
            bool applied = true)
        {
            Unit = unit ?? throw new ArgumentNullException(nameof(unit));
            Evidence = evidence;
            PreviousUnderstanding = previousUnderstanding;
            UpdatedUnderstanding = updatedUnderstanding;
            Origin = origin;
            EngagementVerified = engagementVerified;
            Applied = applied;
        }

        public SemanticUnit Unit { get; }
        public LearningEvidenceKind Evidence { get; }
        public double PreviousUnderstanding { get; }
        public double UpdatedUnderstanding { get; }
        public LearningObservationOrigin Origin { get; }
        public bool EngagementVerified { get; }
        public bool Applied { get; }
        public double Delta => UpdatedUnderstanding - PreviousUnderstanding;
    }

    /// <summary>
    /// Converts action-aware observations into bounded learner-state updates.
    ///
    /// This class is an observation updater, not a learning-transition model. It therefore refuses to
    /// infer mastery from passive assisted exposure or silent completion. A future transition model may
    /// predict learning/forgetting between observations, but that prediction must remain separate.
    /// </summary>
    public sealed class LearnerAdaptationEngine
    {
        private readonly IMutableLearnerModel learner;
        private readonly LearnerAdaptationPolicy policy;

        public LearnerAdaptationEngine(
            IMutableLearnerModel learner,
            LearnerAdaptationPolicy? policy = null)
        {
            this.learner = learner ?? throw new ArgumentNullException(nameof(learner));
            this.policy = policy ?? new LearnerAdaptationPolicy();
        }

        public LearnerUpdate Apply(SemanticUnit unit, LearningEvidenceKind evidence)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            return Apply(LearningObservation.ForEvidence(unit, evidence));
        }

        public LearnerUpdate Apply(LearningObservation observation)
        {
            if (observation == null) throw new ArgumentNullException(nameof(observation));

            var previous = learner.Estimate(observation.Unit).Understanding;
            double updated;

            switch (observation.Evidence)
            {
                case LearningEvidenceKind.AssistedExposure:
                case LearningEvidenceKind.CompletedWithoutAssistance:
                    // No-evidence principle: these events are not state-dependent observations by themselves.
                    // Do not even write the unchanged value, because that would incorrectly turn an implicit
                    // prior into an explicit learner entry.
                    return new LearnerUpdate(
                        observation.Unit,
                        observation.Evidence,
                        previous,
                        previous,
                        observation.Origin,
                        observation.EngagementVerified,
                        applied: false);

                case LearningEvidenceKind.AssistanceRequested:
                    EnsureEngagement(observation);
                    EnsureOrigin(
                        observation,
                        LearningObservationOrigin.SourceDisplay,
                        LearningObservationOrigin.AssistedDisplay);
                    updated = MoveToward(previous, 0.0, policy.AssistanceRequestLoss);
                    break;

                case LearningEvidenceKind.RecallSucceeded:
                    EnsureEngagement(observation);
                    EnsureOrigin(observation, LearningObservationOrigin.RecallProbe);
                    updated = MoveToward(previous, 1.0, policy.RecallSuccessGain);
                    break;

                case LearningEvidenceKind.RecallFailed:
                    EnsureEngagement(observation);
                    EnsureOrigin(observation, LearningObservationOrigin.RecallProbe);
                    updated = MoveToward(previous, 0.0, policy.RecallFailureLoss);
                    break;

                case LearningEvidenceKind.MarkedKnown:
                    EnsureEngagement(observation);
                    EnsureOrigin(observation, LearningObservationOrigin.ExplicitSelfReport);
                    updated = policy.MarkedKnownTarget;
                    break;

                case LearningEvidenceKind.MarkedUnknown:
                    EnsureEngagement(observation);
                    EnsureOrigin(observation, LearningObservationOrigin.ExplicitSelfReport);
                    updated = policy.MarkedUnknownTarget;
                    break;

                case LearningEvidenceKind.VerifiedUnaidedSuccess:
                    EnsureEngagement(observation);
                    EnsureOrigin(observation, LearningObservationOrigin.SourceDisplay);
                    updated = MoveToward(previous, 1.0, policy.VerifiedUnaidedSuccessGain);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(observation),
                        observation.Evidence,
                        "Unknown learning evidence kind.");
            }

            learner.SetUnderstanding(observation.Unit.Text, updated);
            return new LearnerUpdate(
                observation.Unit,
                observation.Evidence,
                previous,
                updated,
                observation.Origin,
                observation.EngagementVerified,
                applied: true);
        }

        private static void EnsureEngagement(LearningObservation observation)
        {
            if (!observation.EngagementVerified)
            {
                throw new InvalidOperationException(
                    "Learning evidence " + observation.Evidence +
                    " requires verified engagement; silent/non-engaged events cannot update learner state.");
            }
        }

        private static void EnsureOrigin(
            LearningObservation observation,
            params LearningObservationOrigin[] allowedOrigins)
        {
            for (var i = 0; i < allowedOrigins.Length; i++)
            {
                if (observation.Origin == allowedOrigins[i]) return;
            }

            throw new InvalidOperationException(
                "Learning evidence " + observation.Evidence +
                " is incompatible with observation origin " + observation.Origin + ".");
        }

        private static double MoveToward(double current, double target, double rate)
        {
            if (current == target || rate == 0.0) return current;
            var updated = current + ((target - current) * rate);
            return updated < 0.0 ? 0.0 : updated > 1.0 ? 1.0 : updated;
        }
    }
}
