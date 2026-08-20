using System;
using System.Collections.Generic;
using PhraseLayer.Core.Semantics;

namespace PhraseLayer.Core.Learning
{
    /// <summary>
    /// Observable or explicit evidence about one semantic unit.
    /// These are product signals, not claims that a single event proves learning.
    /// </summary>
    public enum LearningEvidenceKind
    {
        UnaidedExposure = 0,
        AssistedSuccess = 1,
        AssistanceRequested = 2,
        IncorrectComprehension = 3,
        ExplicitKnown = 4,
        ExplicitUnknown = 5,
    }

    public sealed class LearningEvidence
    {
        public LearningEvidence(
            SemanticUnit unit,
            LearningEvidenceKind kind,
            double reliability = 1.0)
        {
            Unit = unit ?? throw new ArgumentNullException(nameof(unit));
            if (double.IsNaN(reliability) || double.IsInfinity(reliability) || reliability < 0.0 || reliability > 1.0)
                throw new ArgumentOutOfRangeException(nameof(reliability), "Evidence reliability must be finite and within [0,1].");
            Kind = kind;
            Reliability = reliability;
        }

        public SemanticUnit Unit { get; }
        public LearningEvidenceKind Kind { get; }
        public double Reliability { get; }
    }

    /// <summary>
    /// Bounded update rule. A non-direct rule moves the current score toward Target by
    /// AdaptationRate * evidence reliability. Direct rules set the target exactly.
    /// </summary>
    public sealed class LearningEvidenceRule
    {
        public LearningEvidenceRule(double target, double adaptationRate, bool direct = false)
        {
            ValidateProbability(target, nameof(target));
            ValidateProbability(adaptationRate, nameof(adaptationRate));
            Target = target;
            AdaptationRate = adaptationRate;
            Direct = direct;
        }

        public double Target { get; }
        public double AdaptationRate { get; }
        public bool Direct { get; }

        public static LearningEvidenceRule DirectTarget(double target)
        {
            return new LearningEvidenceRule(target, 1.0, direct: true);
        }

        private static void ValidateProbability(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(parameterName, "Value must be finite and within [0,1].");
        }
    }

    /// <summary>
    /// Maps product evidence to bounded score updates.
    ///
    /// ProvisionalV1 is intentionally named as provisional: its coefficients are engineering defaults for
    /// alpha testing, not validated psychometric constants. Experiments can replace the entire policy without
    /// changing persistence, assistance planning, or event collection.
    /// </summary>
    public sealed class LearningAdaptationPolicy
    {
        public LearningAdaptationPolicy(
            LearningEvidenceRule unaidedExposure,
            LearningEvidenceRule assistedSuccess,
            LearningEvidenceRule assistanceRequested,
            LearningEvidenceRule incorrectComprehension,
            LearningEvidenceRule explicitKnown,
            LearningEvidenceRule explicitUnknown)
        {
            UnaidedExposure = unaidedExposure ?? throw new ArgumentNullException(nameof(unaidedExposure));
            AssistedSuccess = assistedSuccess ?? throw new ArgumentNullException(nameof(assistedSuccess));
            AssistanceRequested = assistanceRequested ?? throw new ArgumentNullException(nameof(assistanceRequested));
            IncorrectComprehension = incorrectComprehension ?? throw new ArgumentNullException(nameof(incorrectComprehension));
            ExplicitKnown = explicitKnown ?? throw new ArgumentNullException(nameof(explicitKnown));
            ExplicitUnknown = explicitUnknown ?? throw new ArgumentNullException(nameof(explicitUnknown));
        }

        public LearningEvidenceRule UnaidedExposure { get; }
        public LearningEvidenceRule AssistedSuccess { get; }
        public LearningEvidenceRule AssistanceRequested { get; }
        public LearningEvidenceRule IncorrectComprehension { get; }
        public LearningEvidenceRule ExplicitKnown { get; }
        public LearningEvidenceRule ExplicitUnknown { get; }

        public LearningEvidenceRule GetRule(LearningEvidenceKind kind)
        {
            switch (kind)
            {
                case LearningEvidenceKind.UnaidedExposure: return UnaidedExposure;
                case LearningEvidenceKind.AssistedSuccess: return AssistedSuccess;
                case LearningEvidenceKind.AssistanceRequested: return AssistanceRequested;
                case LearningEvidenceKind.IncorrectComprehension: return IncorrectComprehension;
                case LearningEvidenceKind.ExplicitKnown: return ExplicitKnown;
                case LearningEvidenceKind.ExplicitUnknown: return ExplicitUnknown;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public static LearningAdaptationPolicy ProvisionalV1()
        {
            return new LearningAdaptationPolicy(
                unaidedExposure: new LearningEvidenceRule(target: 1.00, adaptationRate: 0.04),
                assistedSuccess: new LearningEvidenceRule(target: 0.70, adaptationRate: 0.06),
                assistanceRequested: new LearningEvidenceRule(target: 0.15, adaptationRate: 0.25),
                incorrectComprehension: new LearningEvidenceRule(target: 0.05, adaptationRate: 0.35),
                explicitKnown: LearningEvidenceRule.DirectTarget(1.00),
                explicitUnknown: LearningEvidenceRule.DirectTarget(0.00));
        }
    }

    public sealed class LearningUpdateResult
    {
        public LearningUpdateResult(
            LearningEvidence evidence,
            double before,
            double after,
            bool wasExplicit,
            bool mutated)
        {
            Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
            Before = before;
            After = after;
            WasExplicit = wasExplicit;
            Mutated = mutated;
        }

        public LearningEvidence Evidence { get; }
        public double Before { get; }
        public double After { get; }
        public double Delta => After - Before;
        public bool WasExplicit { get; }
        public bool Mutated { get; }
    }

    /// <summary>
    /// Applies one evidence event to a mutable learner model. When the model is PersistentLearnerModel,
    /// the existing persistence wrapper automatically saves the resulting score.
    /// </summary>
    public sealed class LearningEvidenceUpdater
    {
        private readonly LearningAdaptationPolicy policy;

        public LearningEvidenceUpdater(LearningAdaptationPolicy policy)
        {
            this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        public LearningUpdateResult Apply(IMutableLearnerModel learner, LearningEvidence evidence)
        {
            if (learner == null) throw new ArgumentNullException(nameof(learner));
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));

            var estimate = learner.Estimate(evidence.Unit);
            var before = estimate.Understanding;
            var rule = policy.GetRule(evidence.Kind);
            var after = ComputeAfter(before, rule, evidence.Reliability);
            var mutated = Math.Abs(after - before) > 1e-12;
            if (mutated)
                learner.SetUnderstanding(evidence.Unit.Text, after);

            return new LearningUpdateResult(evidence, before, after, estimate.IsExplicit, mutated);
        }

        public static double ComputeAfter(double before, LearningEvidenceRule rule, double reliability)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            if (double.IsNaN(before) || double.IsInfinity(before) || before < 0.0 || before > 1.0)
                throw new ArgumentOutOfRangeException(nameof(before));
            if (double.IsNaN(reliability) || double.IsInfinity(reliability) || reliability < 0.0 || reliability > 1.0)
                throw new ArgumentOutOfRangeException(nameof(reliability));

            if (rule.Direct)
                return reliability <= 0.0 ? before : Lerp(before, rule.Target, reliability);

            var effectiveRate = rule.AdaptationRate * reliability;
            return Lerp(before, rule.Target, effectiveRate);
        }

        private static double Lerp(double from, double to, double amount)
        {
            var value = from + ((to - from) * amount);
            return value < 0.0 ? 0.0 : value > 1.0 ? 1.0 : value;
        }
    }

    /// <summary>
    /// Prevents repeated camera/ASR frames from applying the same evidence over and over during one encounter.
    /// Event producers should use a stable encounter id while a physical passage/conversation turn is being viewed.
    /// </summary>
    public sealed class LearningEncounterEvidenceTracker
    {
        private readonly IMutableLearnerModel learner;
        private readonly LearningEvidenceUpdater updater;
        private readonly HashSet<string> applied = new HashSet<string>(StringComparer.Ordinal);

        public LearningEncounterEvidenceTracker(
            IMutableLearnerModel learner,
            LearningEvidenceUpdater updater)
        {
            this.learner = learner ?? throw new ArgumentNullException(nameof(learner));
            this.updater = updater ?? throw new ArgumentNullException(nameof(updater));
        }

        public LearningUpdateResult? RecordOnce(string encounterId, LearningEvidence evidence)
        {
            if (string.IsNullOrWhiteSpace(encounterId))
                throw new ArgumentException("Encounter id is required.", nameof(encounterId));
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));

            var key = MakeKey(encounterId, evidence);
            if (!applied.Add(key))
                return null;
            return updater.Apply(learner, evidence);
        }

        public void EndEncounter(string encounterId)
        {
            if (string.IsNullOrWhiteSpace(encounterId))
                throw new ArgumentException("Encounter id is required.", nameof(encounterId));
            var prefix = encounterId + "\u001f";
            applied.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
        }

        public void Clear()
        {
            applied.Clear();
        }

        private static string MakeKey(string encounterId, LearningEvidence evidence)
        {
            return encounterId + "\u001f" + evidence.Unit.Id + "\u001f" + ((int)evidence.Kind).ToString();
        }
    }
}
