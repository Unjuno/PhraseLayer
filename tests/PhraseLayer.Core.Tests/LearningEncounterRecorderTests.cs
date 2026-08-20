using System;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Semantics;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class LearningEncounterRecorderTests
    {
        [Fact]
        public void RepeatedObservationProducesOneWeakPositiveOnlyAtEncounterEnd()
        {
            var learner = new InMemoryLearnerModel(0.50);
            var recorder = Recorder(learner);
            var unit = Unit("keep off");

            recorder.Observe("enc-1", unit, reliability: 0.30);
            recorder.Observe("enc-1", unit, reliability: 0.80);
            recorder.Observe("enc-1", unit, reliability: 0.60);

            Assert.Equal(0.50, learner.Estimate(unit).Understanding, 12);
            var results = recorder.EndEncounter("enc-1");

            var update = Assert.Single(results);
            Assert.Equal(LearningEvidenceKind.UnaidedExposure, update.Evidence.Kind);
            Assert.Equal(0.80, update.Evidence.Reliability, 12);
            Assert.Equal(0.58, update.After, 12);
            Assert.Equal(0, recorder.ActiveEncounterCount);
        }

        [Fact]
        public void AssistanceRequestBlocksUnaidedPositiveAndAppliesNegativeImmediately()
        {
            var learner = new InMemoryLearnerModel(0.80);
            var recorder = Recorder(learner);
            var unit = Unit("run out of");

            recorder.Observe("enc-1", unit);
            var immediate = recorder.AssistanceRequested("enc-1", unit);

            Assert.NotNull(immediate);
            Assert.Equal(0.50, learner.Estimate(unit).Understanding, 12);
            Assert.Empty(recorder.EndEncounter("enc-1"));
            Assert.Equal(0.50, learner.Estimate(unit).Understanding, 12);
        }

        [Fact]
        public void AssistedSuccessBlocksUnaidedButMayAddWeakPositiveEvidence()
        {
            var learner = new InMemoryLearnerModel(0.40);
            var recorder = Recorder(learner);
            var unit = Unit("in spite of");

            recorder.Observe("enc-1", unit);
            var assisted = recorder.AssistedSuccess("enc-1", unit);

            Assert.NotNull(assisted);
            Assert.Equal(0.43, learner.Estimate(unit).Understanding, 12);
            Assert.Empty(recorder.EndEncounter("enc-1"));
        }

        [Fact]
        public void DuplicateImmediateSignalIsAppliedOncePerEncounter()
        {
            var learner = new InMemoryLearnerModel(0.80);
            var recorder = Recorder(learner);
            var unit = Unit("run out of");

            var first = recorder.AssistanceRequested("enc-1", unit);
            var duplicate = recorder.AssistanceRequested("enc-1", unit);

            Assert.NotNull(first);
            Assert.Null(duplicate);
            Assert.Equal(0.50, learner.Estimate(unit).Understanding, 12);
        }

        [Fact]
        public void EndEncounterClearsDedupeSoFutureEncounterCanLearnAgain()
        {
            var learner = new InMemoryLearnerModel(0.50);
            var recorder = Recorder(learner);
            var unit = Unit("keep off");

            recorder.Observe("enc-1", unit);
            Assert.Single(recorder.EndEncounter("enc-1"));

            recorder.Observe("enc-1", unit);
            Assert.Single(recorder.EndEncounter("enc-1"));

            Assert.Equal(0.68, learner.Estimate(unit).Understanding, 12);
        }

        [Fact]
        public void CancelEncounterEmitsNoPositiveEvidence()
        {
            var learner = new InMemoryLearnerModel(0.50);
            var recorder = Recorder(learner);
            var unit = Unit("keep off");

            recorder.Observe("enc-1", unit);
            recorder.CancelEncounter("enc-1");

            Assert.Equal(0.50, learner.Estimate(unit).Understanding, 12);
            Assert.Equal(0, recorder.ActiveEncounterCount);
        }

        [Fact]
        public void PersistentLearnerSavesOnlyActualEncounterUpdates()
        {
            var store = new RecordingStore();
            var learner = new PersistentLearnerModel(store, fallbackDefaultUnderstanding: 0.50);
            var recorder = Recorder(learner);
            var unit = Unit("keep off");

            recorder.Observe("enc-1", unit);
            recorder.Observe("enc-1", unit);
            Assert.Equal(0, store.SaveCount);

            Assert.Single(recorder.EndEncounter("enc-1"));
            Assert.Equal(1, store.SaveCount);
        }

        [Fact]
        public void ExplicitKnownBlocksSyntheticUnaidedEvidence()
        {
            var learner = new InMemoryLearnerModel(0.40);
            var recorder = Recorder(learner);
            var unit = Unit("keep off");

            recorder.Observe("enc-1", unit);
            var explicitResult = recorder.ExplicitKnown("enc-1", unit);

            Assert.NotNull(explicitResult);
            Assert.Equal(1.00, learner.Estimate(unit).Understanding, 12);
            Assert.Empty(recorder.EndEncounter("enc-1"));
        }

        private static LearningEncounterRecorder Recorder(IMutableLearnerModel learner)
        {
            var policy = new LearningAdaptationPolicy(
                unaidedExposure: new LearningEvidenceRule(target: 1.00, adaptationRate: 0.20),
                assistedSuccess: new LearningEvidenceRule(target: 0.70, adaptationRate: 0.10),
                assistanceRequested: new LearningEvidenceRule(target: 0.20, adaptationRate: 0.50),
                incorrectComprehension: new LearningEvidenceRule(target: 0.00, adaptationRate: 0.50),
                explicitKnown: LearningEvidenceRule.DirectTarget(1.00),
                explicitUnknown: LearningEvidenceRule.DirectTarget(0.00));
            return new LearningEncounterRecorder(
                new LearningEncounterEvidenceTracker(
                    learner,
                    new LearningEvidenceUpdater(policy)));
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
