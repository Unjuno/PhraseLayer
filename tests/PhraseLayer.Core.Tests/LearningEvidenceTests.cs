using System;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Semantics;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class LearningEvidenceTests
    {
        [Fact]
        public void UnaidedEvidenceMovesScoreTowardTarget()
        {
            var learner = new InMemoryLearnerModel(0.50);
            var updater = new LearningEvidenceUpdater(Policy(
                unaided: new LearningEvidenceRule(target: 1.00, adaptationRate: 0.20)));

            var result = updater.Apply(
                learner,
                new LearningEvidence(Unit("keep off"), LearningEvidenceKind.UnaidedExposure));

            Assert.Equal(0.50, result.Before, 12);
            Assert.Equal(0.60, result.After, 12);
            Assert.Equal(0.10, result.Delta, 12);
            Assert.True(result.Mutated);
            Assert.False(result.WasExplicit);
            Assert.Equal(0.60, learner.Estimate(Unit("keep off")).Understanding, 12);
        }

        [Fact]
        public void ReliabilityScalesUpdateStrength()
        {
            var after = LearningEvidenceUpdater.ComputeAfter(
                before: 0.50,
                rule: new LearningEvidenceRule(target: 1.00, adaptationRate: 0.20),
                reliability: 0.50);

            Assert.Equal(0.55, after, 12);
        }

        [Fact]
        public void AssistanceRequestedCanDecreaseScore()
        {
            var learner = new InMemoryLearnerModel(0.80);
            var updater = new LearningEvidenceUpdater(Policy(
                assistanceRequested: new LearningEvidenceRule(target: 0.20, adaptationRate: 0.50)));

            var result = updater.Apply(
                learner,
                new LearningEvidence(Unit("run out of"), LearningEvidenceKind.AssistanceRequested));

            Assert.Equal(0.50, result.After, 12);
            Assert.True(result.Delta < 0.0);
        }

        [Fact]
        public void DirectKnownSetsExactTargetAtFullReliability()
        {
            var learner = new InMemoryLearnerModel(0.40);
            var updater = new LearningEvidenceUpdater(Policy(
                explicitKnown: LearningEvidenceRule.DirectTarget(1.00)));

            var result = updater.Apply(
                learner,
                new LearningEvidence(Unit("keep off"), LearningEvidenceKind.ExplicitKnown));

            Assert.Equal(1.00, result.After, 12);
        }

        [Fact]
        public void DirectRuleInterpolatesAtPartialReliability()
        {
            var after = LearningEvidenceUpdater.ComputeAfter(
                before: 0.40,
                rule: LearningEvidenceRule.DirectTarget(1.00),
                reliability: 0.50);

            Assert.Equal(0.70, after, 12);
        }

        [Fact]
        public void ZeroReliabilityDoesNotMutateOrPersist()
        {
            var store = new RecordingStore();
            var learner = new PersistentLearnerModel(store, fallbackDefaultUnderstanding: 0.50);
            var updater = new LearningEvidenceUpdater(Policy(
                unaided: new LearningEvidenceRule(target: 1.00, adaptationRate: 0.50)));

            var result = updater.Apply(
                learner,
                new LearningEvidence(
                    Unit("keep off"),
                    LearningEvidenceKind.UnaidedExposure,
                    reliability: 0.0));

            Assert.False(result.Mutated);
            Assert.Equal(0.50, result.After, 12);
            Assert.Equal(0, store.SaveCount);
        }

        [Fact]
        public void PersistentLearnerSavesEvidenceMutationOnce()
        {
            var store = new RecordingStore();
            var learner = new PersistentLearnerModel(store, fallbackDefaultUnderstanding: 0.50);
            var updater = new LearningEvidenceUpdater(Policy(
                unaided: new LearningEvidenceRule(target: 1.00, adaptationRate: 0.20)));

            updater.Apply(
                learner,
                new LearningEvidence(Unit("keep off"), LearningEvidenceKind.UnaidedExposure));

            Assert.Equal(1, store.SaveCount);
            var saved = Assert.IsType<LearnerProfileSnapshot>(store.Saved);
            var entry = Assert.Single(saved.Entries);
            Assert.Equal("keep off", entry.Text);
            Assert.Equal(0.60, entry.Understanding, 12);
        }

        [Fact]
        public void TrackerSuppressesDuplicateEvidenceWithinEncounter()
        {
            var learner = new InMemoryLearnerModel(0.50);
            var tracker = new LearningEncounterEvidenceTracker(
                learner,
                new LearningEvidenceUpdater(Policy(
                    unaided: new LearningEvidenceRule(target: 1.00, adaptationRate: 0.20))));
            var evidence = new LearningEvidence(Unit("keep off"), LearningEvidenceKind.UnaidedExposure);

            var first = tracker.RecordOnce("encounter-1", evidence);
            var duplicate = tracker.RecordOnce("encounter-1", evidence);

            Assert.NotNull(first);
            Assert.Null(duplicate);
            Assert.Equal(0.60, learner.Estimate(Unit("keep off")).Understanding, 12);
        }

        [Fact]
        public void TrackerAllowsDifferentKindsWithinSameEncounter()
        {
            var learner = new InMemoryLearnerModel(0.50);
            var tracker = new LearningEncounterEvidenceTracker(
                learner,
                new LearningEvidenceUpdater(Policy(
                    unaided: new LearningEvidenceRule(target: 1.00, adaptationRate: 0.20),
                    assistanceRequested: new LearningEvidenceRule(target: 0.00, adaptationRate: 0.50))));
            var unit = Unit("keep off");

            var positive = tracker.RecordOnce(
                "encounter-1",
                new LearningEvidence(unit, LearningEvidenceKind.UnaidedExposure));
            var negative = tracker.RecordOnce(
                "encounter-1",
                new LearningEvidence(unit, LearningEvidenceKind.AssistanceRequested));

            Assert.NotNull(positive);
            Assert.NotNull(negative);
            Assert.Equal(0.30, learner.Estimate(unit).Understanding, 12);
        }

        [Fact]
        public void EndingEncounterAllowsSameEvidenceAgain()
        {
            var learner = new InMemoryLearnerModel(0.50);
            var tracker = new LearningEncounterEvidenceTracker(
                learner,
                new LearningEvidenceUpdater(Policy(
                    unaided: new LearningEvidenceRule(target: 1.00, adaptationRate: 0.20))));
            var evidence = new LearningEvidence(Unit("keep off"), LearningEvidenceKind.UnaidedExposure);

            Assert.NotNull(tracker.RecordOnce("encounter-1", evidence));
            tracker.EndEncounter("encounter-1");
            Assert.NotNull(tracker.RecordOnce("encounter-1", evidence));

            Assert.Equal(0.68, learner.Estimate(Unit("keep off")).Understanding, 12);
        }

        [Fact]
        public void TrackerTreatsDifferentUnitIdsSeparately()
        {
            var learner = new InMemoryLearnerModel(0.50);
            var tracker = new LearningEncounterEvidenceTracker(
                learner,
                new LearningEvidenceUpdater(Policy(
                    unaided: new LearningEvidenceRule(target: 1.00, adaptationRate: 0.20))));
            var first = new SemanticUnit("mwe:0:8", SemanticUnitKind.MultiwordExpression, 0, 8, "keep off", 2);
            var second = new SemanticUnit("mwe:20:8", SemanticUnitKind.MultiwordExpression, 20, 8, "keep off", 2);

            Assert.NotNull(tracker.RecordOnce(
                "encounter-1",
                new LearningEvidence(first, LearningEvidenceKind.UnaidedExposure)));
            Assert.NotNull(tracker.RecordOnce(
                "encounter-1",
                new LearningEvidence(second, LearningEvidenceKind.UnaidedExposure)));

            Assert.Equal(0.68, learner.Estimate(Unit("keep off")).Understanding, 12);
        }

        [Theory]
        [InlineData(-0.01)]
        [InlineData(1.01)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void EvidenceRejectsInvalidReliability(double reliability)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LearningEvidence(
                    Unit("keep off"),
                    LearningEvidenceKind.UnaidedExposure,
                    reliability));
        }

        private static LearningAdaptationPolicy Policy(
            LearningEvidenceRule unaided = null,
            LearningEvidenceRule assistedSuccess = null,
            LearningEvidenceRule assistanceRequested = null,
            LearningEvidenceRule incorrect = null,
            LearningEvidenceRule explicitKnown = null,
            LearningEvidenceRule explicitUnknown = null)
        {
            var neutral = new LearningEvidenceRule(target: 0.50, adaptationRate: 0.00);
            return new LearningAdaptationPolicy(
                unaided ?? neutral,
                assistedSuccess ?? neutral,
                assistanceRequested ?? neutral,
                incorrect ?? neutral,
                explicitKnown ?? neutral,
                explicitUnknown ?? neutral);
        }

        private static SemanticUnit Unit(string text)
        {
            return new SemanticUnit(
                "unit:" + text,
                SemanticUnitKind.MultiwordExpression,
                start: 0,
                length: text.Length,
                text: text,
                tokenCount: Math.Max(1, text.Split(' ').Length));
        }

        private sealed class RecordingStore : ILearnerProfileStore
        {
            public LearnerProfileSnapshot Loaded { get; set; }
            public LearnerProfileSnapshot Saved { get; private set; }
            public int SaveCount { get; private set; }

            public LearnerProfileSnapshot Load() => Loaded;

            public void Save(LearnerProfileSnapshot snapshot)
            {
                Saved = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
                SaveCount++;
            }
        }
    }
}
