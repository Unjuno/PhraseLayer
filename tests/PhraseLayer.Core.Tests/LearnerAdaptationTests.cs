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
        public void AssistedExposureDoesNotMutateOrCreateExplicitKnowledge()
        {
            var unit = GetKeepOffUnit();
            var learner = new InMemoryLearnerModel(0.10);
            var engine = new LearnerAdaptationEngine(learner);

            var update = engine.Apply(unit, LearningEvidenceKind.AssistedExposure);
            var estimate = learner.Estimate(unit);

            Assert.False(update.Applied);
            Assert.Equal(0.10, update.PreviousUnderstanding, 12);
            Assert.Equal(0.10, update.UpdatedUnderstanding, 12);
            Assert.False(estimate.IsExplicit);
            Assert.Equal(0.10, estimate.Understanding, 12);
        }

        [Fact]
        public void SilentCompletionDoesNotBecomeMasteryEvidence()
        {
            var unit = GetKeepOffUnit();
            var learner = new InMemoryLearnerModel(0.20);
            var engine = new LearnerAdaptationEngine(learner);

            for (var i = 0; i < 100; i++)
            {
                var update = engine.Apply(unit, LearningEvidenceKind.CompletedWithoutAssistance);
                Assert.False(update.Applied);
            }

            var estimate = learner.Estimate(unit);
            Assert.False(estimate.IsExplicit);
            Assert.Equal(0.20, estimate.Understanding, 12);
        }

        [Fact]
        public void VerifiedUnaidedSuccessRaisesUnderstandingAndReducesAutoSupport()
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
                engine.Apply(unit, LearningEvidenceKind.VerifiedUnaidedSuccess);

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
            var update = engine.Apply(new LearningObservation(
                unit,
                LearningEvidenceKind.AssistanceRequested,
                LearningObservationOrigin.SourceDisplay,
                engagementVerified: true));

            var decision = Assert.Single(planner.Plan(document, learner, policy).Decisions);
            Assert.Equal("keep off", decision.Unit.Text, ignoreCase: true);
            Assert.True(decision.EstimatedUnderstanding < policy.PreserveKnownThreshold);
            Assert.Equal(LearningObservationOrigin.SourceDisplay, update.Origin);
            Assert.True(update.EngagementVerified);
        }

        [Fact]
        public void AssistanceRequestWithoutDisplayActionIsRejected()
        {
            var unit = GetKeepOffUnit();
            var learner = new InMemoryLearnerModel(0.55);
            var engine = new LearnerAdaptationEngine(learner);

            Assert.Throws<InvalidOperationException>(() =>
                engine.Apply(unit, LearningEvidenceKind.AssistanceRequested));
        }

        [Fact]
        public void RecallEvidenceRecordsProbeOrigin()
        {
            var unit = GetKeepOffUnit();
            var learner = new InMemoryLearnerModel(0.55);
            var engine = new LearnerAdaptationEngine(learner);

            var update = engine.Apply(unit, LearningEvidenceKind.RecallSucceeded);

            Assert.True(update.Applied);
            Assert.Equal(LearningObservationOrigin.RecallProbe, update.Origin);
            Assert.True(update.EngagementVerified);
            Assert.True(update.UpdatedUnderstanding > update.PreviousUnderstanding);
        }

        [Fact]
        public void IncompatibleObservationOriginIsRejected()
        {
            var unit = GetKeepOffUnit();
            var learner = new InMemoryLearnerModel(0.55);
            var engine = new LearnerAdaptationEngine(learner);
            var invalid = new LearningObservation(
                unit,
                LearningEvidenceKind.RecallSucceeded,
                LearningObservationOrigin.SourceDisplay,
                engagementVerified: true);

            Assert.Throws<InvalidOperationException>(() => engine.Apply(invalid));
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
            Assert.Equal(LearningObservationOrigin.ExplicitSelfReport, known.Origin);
            Assert.Equal(KnowledgeState.Known, learner.Estimate(unit).State);

            var unknown = engine.Apply(unit, LearningEvidenceKind.MarkedUnknown);
            Assert.Equal(0.08, unknown.UpdatedUnderstanding, 12);
            Assert.Equal(KnowledgeState.Unknown, learner.Estimate(unit).State);
        }

        [Fact]
        public void PersistentLearnerDoesNotSaveNoEvidenceButSavesRecall()
        {
            var unit = GetKeepOffUnit();
            var store = new CountingStore();
            var learner = new PersistentLearnerModel(store, 0.55);
            var engine = new LearnerAdaptationEngine(learner);

            var passive = engine.Apply(unit, LearningEvidenceKind.AssistedExposure);
            Assert.False(passive.Applied);
            Assert.Equal(0, store.SaveCount);

            engine.Apply(unit, LearningEvidenceKind.RecallSucceeded);

            Assert.Equal(1, store.SaveCount);
            Assert.NotNull(store.LastSaved);
            Assert.Single(store.LastSaved!.Entries);
        }

        [Fact]
        public void UpdatePolicyRejectsNonFiniteOrOutOfRangeRates()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LearnerAdaptationPolicy(assistedExposureGain: double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LearnerAdaptationPolicy(recallFailureLoss: 1.01));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LearnerAdaptationPolicy(verifiedUnaidedSuccessGain: -0.01));
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
