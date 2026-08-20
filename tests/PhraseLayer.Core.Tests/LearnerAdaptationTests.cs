using System;
using System.Linq;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Semantics;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class LearnerAdaptationTests
    {
        [Fact]
        public void AssistedExposureCannotPromotePassiveUseToKnown()
        {
            var unit = GetKeepOffUnit();
            var learner = new InMemoryLearnerModel(0.10);
            learner.SetUnderstanding(unit.Text, 0.10);
            var engine = new LearnerAdaptationEngine(learner);

            for (var i = 0; i < 250; i++)
                engine.Apply(unit, LearningEvidenceKind.AssistedExposure);

            var estimate = learner.Estimate(unit);
            Assert.InRange(estimate.Understanding, 0.79, 0.80);
            Assert.NotEqual(KnowledgeState.Known, estimate.State);
        }

        [Fact]
        public void AssistedExposureDoesNotLowerExistingMastery()
        {
            var unit = GetKeepOffUnit();
            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding(unit.Text, 0.95);
            var engine = new LearnerAdaptationEngine(learner);

            var update = engine.Apply(unit, LearningEvidenceKind.AssistedExposure);

            Assert.Equal(0.95, update.PreviousUnderstanding, 12);
            Assert.Equal(0.95, update.UpdatedUnderstanding, 12);
            Assert.Equal(KnowledgeState.Known, learner.Estimate(unit).State);
        }

        [Fact]
        public void UnassistedCompletionRaisesUnderstandingAndReducesAutoSupport()
        {
            var segmenter = new RuleBasedSemanticSegmenter(new[] { "keep off" });
            var document = segmenter.Segment("Please keep off the grass.");
            var unit = Assert.Single(document.OfKind(SemanticUnitKind.MultiwordExpression));
            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding(unit.Text, 0.10);
            var planner = new AssistancePlanner();
            var policy = AssistancePolicy.ForMode(AssistanceMode.Auto);
            var before = planner.Plan(document, learner, policy);
            var engine = new LearnerAdaptationEngine(learner);

            for (var i = 0; i < 20; i++)
                engine.Apply(unit, LearningEvidenceKind.CompletedWithoutAssistance);

            var after = planner.Plan(document, learner, policy);
            Assert.True(learner.Estimate(unit).Understanding > 0.80);
            Assert.True(after.TargetRatio < before.TargetRatio);
        }

        [Fact]
        public void AssistanceRequestCanReintroducePreviouslySuppressedHelp()
        {
            var segmenter = new RuleBasedSemanticSegmenter(new[] { "keep off" });
            var document = segmenter.Segment("Please keep off the grass.");
            var unit = Assert.Single(document.OfKind(SemanticUnitKind.MultiwordExpression));
            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding(unit.Text, 0.90);
            var planner = new AssistancePlanner();
            var policy = AssistancePolicy.ForMode(AssistanceMode.Balanced);
            Assert.Empty(planner.Plan(document, learner, policy).Decisions);

            var engine = new LearnerAdaptationEngine(learner);
            engine.Apply(unit, LearningEvidenceKind.AssistanceRequested);

            var decision = Assert.Single(planner.Plan(document, learner, policy).Decisions);
            Assert.Equal("keep off", decision.Unit.Text, ignoreCase: true);
            Assert.True(decision.EstimatedUnderstanding < policy.PreserveKnownThreshold);
        }

        [Fact]
        public void ExplicitKnownAndUnknownSignalsUseReviewedTargets()
        {
            var unit = GetKeepOffUnit();
            var learner = new InMemoryLearnerModel(0.55);
            var policy = new LearnerAdaptationPolicy(markedKnownTarget: 0.97, markedUnknownTarget: 0.08);
            var engine = new LearnerAdaptationEngine(learner, policy);

            var known = engine.Apply(unit, LearningEvidenceKind.MarkedKnown);
            Assert.Equal(0.97, known.UpdatedUnderstanding, 12);
            Assert.Equal(KnowledgeState.Known, learner.Estimate(unit).State);

            var unknown = engine.Apply(unit, LearningEvidenceKind.MarkedUnknown);
            Assert.Equal(0.08, unknown.UpdatedUnderstanding, 12);
            Assert.Equal(KnowledgeState.Unknown, learner.Estimate(unit).State);
        }

        [Fact]
        public void PersistentLearnerSavesEachAdaptationMutation()
        {
            var unit = GetKeepOffUnit();
            var store = new CountingStore();
            var learner = new PersistentLearnerModel(store, 0.55);
            var engine = new LearnerAdaptationEngine(learner);

            engine.Apply(unit, LearningEvidenceKind.RecallSucceeded);

            Assert.Equal(1, store.SaveCount);
            Assert.NotNull(store.LastSaved);
            Assert.Single(store.LastSaved!.Entries);
        }

        [Fact]
        public void UpdatePolicyRejectsNonFiniteOrOutOfRangeRates()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LearnerAdaptationPolicy(assistedExposureGain: double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LearnerAdaptationPolicy(recallFailureLoss: 1.01));
        }

        private static SemanticUnit GetKeepOffUnit()
        {
            var document = new RuleBasedSemanticSegmenter(new[] { "keep off" })
                .Segment("Please keep off the grass.");
            return Assert.Single(document.OfKind(SemanticUnitKind.MultiwordExpression));
        }

        private sealed class CountingStore : ILearnerProfileStore
        {
            public int SaveCount { get; private set; }
            public LearnerProfileSnapshot? LastSaved { get; private set; }

            public LearnerProfileSnapshot? Load() => null;

            public void Save(LearnerProfileSnapshot snapshot)
            {
                SaveCount++;
                LastSaved = snapshot;
            }
        }
    }
}
