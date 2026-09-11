using System;
using System.Collections.Generic;
using System.Linq;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class LearnerCommitSafetyTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FailedSaveRetryUsesOneFrozenSnapshotWithoutDoubleLearning(bool commitBeforeThrow)
        {
            var store = new FaultingStore(commitBeforeThrow);
            var model = new PersistentLearnerModel(store);
            var plan = Plan();
            var words = plan.Document!.OfKind(SemanticUnitKind.Word).ToArray();
            var session = new LearningEncounterSession(plan, new LearnerAdaptationEngine(model));
            Assert.Throws<InjectedFailure>(() => session.Finish(true));
            Assert.True(session.IsCommitPending);
            Assert.False(session.IsFinished);
            Assert.All(words, word => Assert.Equal(0.5, model.Estimate(word).Understanding));
            Assert.Throws<InvalidOperationException>(() => session.Record(words[0], LearningEvidenceKind.RecallFailed));
            Assert.Throws<InvalidOperationException>(() => session.Finish(false));
            Assert.Equal(1, store.Attempts);
            var firstAttempt = store.Attempted[0];
            var summary = session.Finish(true);
            Assert.All(words, word => Assert.Equal(0.54, model.Estimate(word).Understanding, 12));
            Assert.Equal(2, store.Attempts);
            Assert.Equal(firstAttempt.Entries.Select(item => item.Understanding), store.Stored.Entries.Select(item => item.Understanding));
            Assert.Equal(2, summary.Updates.Count);
            Assert.Same(summary, session.Finish(true));
            Assert.Equal(2, store.Attempts);
            Assert.False(session.IsCommitPending);
        }
        [Fact]
        public void InterveningEvidenceCannotBeOverwrittenByAnOldPreparedBatch()
        {
            var model = new InMemoryLearnerModel(0.5);
            var word = Plan().Document!.OfKind(SemanticUnitKind.Word).First();
            var batch = new LearnerAdaptationEngine(model).PrepareBatch(new[] { new LearnerEvidence(word, LearningEvidenceKind.RecallSucceeded) });
            Assert.Equal(0.5, model.Estimate(word).Understanding);
            model.SetUnderstanding("external", 0.9);
            Assert.Throws<InvalidOperationException>(() => batch.Commit());
            Assert.False(batch.IsCommitted);
            Assert.Equal(0.5, model.Estimate(word).Understanding);
            Assert.Contains(model.CreateSnapshot().Entries, item => item.Text == "external" && item.Understanding == 0.9);
        }
        [Fact]
        public void LatePreparationFailureDoesNotMutateAnyRealScores()
        {
            var model = new InMemoryLearnerModel(0.5);
            var word = Plan().Document!.OfKind(SemanticUnitKind.Word).First();
            Assert.Throws<ArgumentException>(() => new LearnerAdaptationEngine(model).PrepareBatch(new LearnerEvidence[]
                { new LearnerEvidence(word, LearningEvidenceKind.RecallSucceeded), null! }));
            Assert.Equal(0.5, model.Estimate(word).Understanding);
            Assert.Empty(model.CreateSnapshot().Entries);
        }
        [Fact]
        public void PreparationUsesTheConfiguredPolicyAndCommitIsIdempotent()
        {
            var model = new InMemoryLearnerModel(0.5);
            var word = Plan().Document!.OfKind(SemanticUnitKind.Word).First();
            var batch = new LearnerAdaptationEngine(model, new LearnerAdaptationPolicy(recallSuccessGain: 0.4))
                .PrepareBatch(new[] { new LearnerEvidence(word, LearningEvidenceKind.RecallSucceeded) });
            Assert.Equal(0.7, Assert.Single(batch.Updates).UpdatedUnderstanding, 12);
            Assert.Empty(model.CreateSnapshot().Entries);
            batch.Commit();
            batch.Commit();
            Assert.Equal(0.7, model.Estimate(word).Understanding, 12);
        }
        [Fact]
        public void SnapshotBackingCollectionCannotBeMutatedByCasting()
        {
            var snapshot = new LearnerProfileSnapshot(0.5, new[] { new LearnerKnowledgeEntry("alpha", 0.4) });
            var list = Assert.IsAssignableFrom<IList<LearnerKnowledgeEntry>>(snapshot.Entries);
            Assert.Throws<NotSupportedException>(() => list[0] = new LearnerKnowledgeEntry("alpha", 0.9));
            Assert.Equal(0.4, snapshot.Entries[0].Understanding);
        }
        [Fact]
        public void PersistentDirectMutationPublishesOnlyAfterSaveReturns()
        {
            var store = new FaultingStore(false);
            var model = new PersistentLearnerModel(store);
            var word = Plan().Document!.OfKind(SemanticUnitKind.Word).First();
            Assert.Throws<InjectedFailure>(() => model.SetUnderstanding(word.Text, 0.9));
            Assert.Equal(0.5, model.Estimate(word).Understanding);
            Assert.Empty(model.CreateSnapshot().Entries);
        }
        [Fact]
        public void EmptyEncounterDoesNotWriteAnUnchangedProfile()
        {
            var store = new FaultingStore(false);
            var session = new LearningEncounterSession(Plan(), new LearnerAdaptationEngine(new PersistentLearnerModel(store)));
            Assert.Empty(session.Finish().Updates);
            Assert.Equal(0, store.Attempts);
        }
        private static MixedLanguagePlan Plan()
        {
            const string source = "alpha beta.";
            return new MixedLanguagePlan(source, new[] { new MixedLanguageSegment(source, source, false, null) },
                new AssistancePlan(Array.Empty<AssistanceDecision>(), 0, 0), new RuleBasedSemanticSegmenter().Segment(source));
        }
        private sealed class InjectedFailure : Exception { }
        private sealed class FaultingStore : ILearnerProfileStore
        {
            private readonly bool commitBeforeThrow;
            public FaultingStore(bool commitBeforeThrow) { this.commitBeforeThrow = commitBeforeThrow; }
            public LearnerProfileSnapshot Stored { get; private set; } = new InMemoryLearnerModel(0.5).CreateSnapshot();
            public List<LearnerProfileSnapshot> Attempted { get; } = new List<LearnerProfileSnapshot>();
            public int Attempts => Attempted.Count;
            public LearnerProfileSnapshot? Load() => Stored;
            public void Save(LearnerProfileSnapshot snapshot)
            {
                Attempted.Add(snapshot);
                if (Attempts == 1)
                {
                    if (commitBeforeThrow) Stored = snapshot;
                    throw new InjectedFailure();
                }
                Stored = snapshot;
            }
        }
    }
}
