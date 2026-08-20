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
        ComprehensionFailed = 7
    }

    /// <summary>
    /// Tunable learning-update policy.
    /// Passive assisted exposure is deliberately capped below the Known threshold so the system
    /// cannot conclude mastery merely because a translation was shown repeatedly.
    /// These defaults are alpha engineering parameters, not validated psychometric constants.
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
            double markedUnknownTarget = 0.10,
            double comprehensionFailureLoss = 0.30)
        {
            AssistedExposureGain = Validate01(assistedExposureGain, nameof(assistedExposureGain));
            AssistedExposureCeiling = Validate01(assistedExposureCeiling, nameof(assistedExposureCeiling));
            UnassistedCompletionGain = Validate01(unassistedCompletionGain, nameof(unassistedCompletionGain));
            AssistanceRequestLoss = Validate01(assistanceRequestLoss, nameof(assistanceRequestLoss));
            RecallSuccessGain = Validate01(recallSuccessGain, nameof(recallSuccessGain));
            RecallFailureLoss = Validate01(recallFailureLoss, nameof(recallFailureLoss));
            MarkedKnownTarget = Validate01(markedKnownTarget, nameof(markedKnownTarget));
            MarkedUnknownTarget = Validate01(markedUnknownTarget, nameof(markedUnknownTarget));
            ComprehensionFailureLoss = Validate01(comprehensionFailureLoss, nameof(comprehensionFailureLoss));
        }

        public double AssistedExposureGain { get; }
        public double AssistedExposureCeiling { get; }
        public double UnassistedCompletionGain { get; }
        public double AssistanceRequestLoss { get; }
        public double RecallSuccessGain { get; }
        public double RecallFailureLoss { get; }
        public double MarkedKnownTarget { get; }
        public double MarkedUnknownTarget { get; }
        public double ComprehensionFailureLoss { get; }

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
            : this(unit, evidence, previousUnderstanding, updatedUnderstanding, 1.0)
        {
        }

        public LearnerUpdate(
            SemanticUnit unit,
            LearningEvidenceKind evidence,
            double previousUnderstanding,
            double updatedUnderstanding,
            double reliability)
        {
            Unit = unit ?? throw new ArgumentNullException(nameof(unit));
            Evidence = evidence;
            PreviousUnderstanding = previousUnderstanding;
            UpdatedUnderstanding = updatedUnderstanding;
            Reliability = ValidateReliability(reliability);
        }

        public SemanticUnit Unit { get; }
        public LearningEvidenceKind Evidence { get; }
        public double PreviousUnderstanding { get; }
        public double UpdatedUnderstanding { get; }
        public double Reliability { get; }
        public double Delta => UpdatedUnderstanding - PreviousUnderstanding;
        public bool Mutated => Math.Abs(Delta) > 1e-12;

        private static double ValidateReliability(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(nameof(value), "Reliability must be finite and within [0,1].");
            return value;
        }
    }

    /// <summary>
    /// Converts observable learner evidence into bounded understanding-score updates.
    /// Reliability scales the configured rate so noisy inferred evidence can be weaker than explicit user signals.
    /// No-op updates are not persisted.
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

        public LearnerUpdate Apply(
            SemanticUnit unit,
            LearningEvidenceKind evidence,
            double reliability = 1.0)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            ValidateReliability(reliability);

            var previous = learner.Estimate(unit).Understanding;
            double updated;
            switch (evidence)
            {
                case LearningEvidenceKind.AssistedExposure:
                    updated = previous >= policy.AssistedExposureCeiling
                        ? previous
                        : MoveToward(
                            previous,
                            policy.AssistedExposureCeiling,
                            policy.AssistedExposureGain * reliability);
                    break;
                case LearningEvidenceKind.CompletedWithoutAssistance:
                    updated = MoveToward(
                        previous,
                        1.0,
                        policy.UnassistedCompletionGain * reliability);
                    break;
                case LearningEvidenceKind.AssistanceRequested:
                    updated = MoveToward(
                        previous,
                        0.0,
                        policy.AssistanceRequestLoss * reliability);
                    break;
                case LearningEvidenceKind.RecallSucceeded:
                    updated = MoveToward(
                        previous,
                        1.0,
                        policy.RecallSuccessGain * reliability);
                    break;
                case LearningEvidenceKind.RecallFailed:
                    updated = MoveToward(
                        previous,
                        0.0,
                        policy.RecallFailureLoss * reliability);
                    break;
                case LearningEvidenceKind.ComprehensionFailed:
                    updated = MoveToward(
                        previous,
                        0.0,
                        policy.ComprehensionFailureLoss * reliability);
                    break;
                case LearningEvidenceKind.MarkedKnown:
                    updated = MoveToward(previous, policy.MarkedKnownTarget, reliability);
                    break;
                case LearningEvidenceKind.MarkedUnknown:
                    updated = MoveToward(previous, policy.MarkedUnknownTarget, reliability);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(evidence), evidence, "Unknown learning evidence kind.");
            }

            if (Math.Abs(updated - previous) > 1e-12)
                learner.SetUnderstanding(unit.Text, updated);
            return new LearnerUpdate(unit, evidence, previous, updated, reliability);
        }

        private static double MoveToward(double current, double target, double rate)
        {
            if (current == target || rate == 0.0) return current;
            var updated = current + ((target - current) * rate);
            return updated < 0.0 ? 0.0 : updated > 1.0 ? 1.0 : updated;
        }

        private static void ValidateReliability(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(nameof(value), "Reliability must be finite and within [0,1].");
        }
    }
}
