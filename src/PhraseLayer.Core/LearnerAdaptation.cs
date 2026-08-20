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
        MarkedUnknown = 6
    }

    /// <summary>
    /// Tunable learning-update policy.
    /// Passive assisted exposure is deliberately capped below the Known threshold so the system
    /// cannot conclude mastery merely because a translation was shown repeatedly.
    /// </summary>
    public sealed class LearnerAdaptationPolicy
    {
        public LearnerAdaptationPolicy(
            double assistedExposureGain = 0.03,
            double assistedExposureCeiling = 0.80,
            double unassistedCompletionGain = 0.08,
            double assistanceRequestLoss = 0.20,
            double recallSuccessGain = 0.20,
            double recallFailureLoss = 0.30,
            double markedKnownTarget = 0.98,
            double markedUnknownTarget = 0.10)
        {
            AssistedExposureGain = Validate01(assistedExposureGain, nameof(assistedExposureGain));
            AssistedExposureCeiling = Validate01(assistedExposureCeiling, nameof(assistedExposureCeiling));
            UnassistedCompletionGain = Validate01(unassistedCompletionGain, nameof(unassistedCompletionGain));
            AssistanceRequestLoss = Validate01(assistanceRequestLoss, nameof(assistanceRequestLoss));
            RecallSuccessGain = Validate01(recallSuccessGain, nameof(recallSuccessGain));
            RecallFailureLoss = Validate01(recallFailureLoss, nameof(recallFailureLoss));
            MarkedKnownTarget = Validate01(markedKnownTarget, nameof(markedKnownTarget));
            MarkedUnknownTarget = Validate01(markedUnknownTarget, nameof(markedUnknownTarget));
        }

        public double AssistedExposureGain { get; }
        public double AssistedExposureCeiling { get; }
        public double UnassistedCompletionGain { get; }
        public double AssistanceRequestLoss { get; }
        public double RecallSuccessGain { get; }
        public double RecallFailureLoss { get; }
        public double MarkedKnownTarget { get; }
        public double MarkedUnknownTarget { get; }

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
            double updatedUnderstanding)
        {
            Unit = unit ?? throw new ArgumentNullException(nameof(unit));
            Evidence = evidence;
            PreviousUnderstanding = previousUnderstanding;
            UpdatedUnderstanding = updatedUnderstanding;
        }

        public SemanticUnit Unit { get; }
        public LearningEvidenceKind Evidence { get; }
        public double PreviousUnderstanding { get; }
        public double UpdatedUnderstanding { get; }
        public double Delta => UpdatedUnderstanding - PreviousUnderstanding;
    }

    /// <summary>
    /// Converts observable learner evidence into bounded understanding-score updates.
    /// The update rule is intentionally simple and replaceable: positive evidence moves toward a ceiling,
    /// negative evidence scales down current confidence, and explicit user labels set reviewed targets.
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

            var previous = learner.Estimate(unit).Understanding;
            double updated;
            switch (evidence)
            {
                case LearningEvidenceKind.AssistedExposure:
                    updated = MoveToward(previous, policy.AssistedExposureCeiling, policy.AssistedExposureGain);
                    break;
                case LearningEvidenceKind.CompletedWithoutAssistance:
                    updated = MoveToward(previous, 1.0, policy.UnassistedCompletionGain);
                    break;
                case LearningEvidenceKind.AssistanceRequested:
                    updated = MoveToward(previous, 0.0, policy.AssistanceRequestLoss);
                    break;
                case LearningEvidenceKind.RecallSucceeded:
                    updated = MoveToward(previous, 1.0, policy.RecallSuccessGain);
                    break;
                case LearningEvidenceKind.RecallFailed:
                    updated = MoveToward(previous, 0.0, policy.RecallFailureLoss);
                    break;
                case LearningEvidenceKind.MarkedKnown:
                    updated = policy.MarkedKnownTarget;
                    break;
                case LearningEvidenceKind.MarkedUnknown:
                    updated = policy.MarkedUnknownTarget;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(evidence), evidence, "Unknown learning evidence kind.");
            }

            learner.SetUnderstanding(unit.Text, updated);
            return new LearnerUpdate(unit, evidence, previous, updated);
        }

        private static double MoveToward(double current, double target, double rate)
        {
            if (current == target || rate == 0.0) return current;
            var updated = current + ((target - current) * rate);
            return updated < 0.0 ? 0.0 : updated > 1.0 ? 1.0 : updated;
        }
    }
}
