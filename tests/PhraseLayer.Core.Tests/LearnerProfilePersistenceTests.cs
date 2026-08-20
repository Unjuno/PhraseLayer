using System;
using System.Linq;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Semantics;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class LearnerProfilePersistenceTests
    {
        [Fact]
        public void SnapshotNormalizesSortsAndCopiesEntries()
        {
            var snapshot = new LearnerProfileSnapshot(
                0.60,
                new[]
                {
                    new LearnerKnowledgeEntry("  Keep   Off ", 0.90),
                    new LearnerKnowledgeEntry("APPLE", 0.40),
                });

            Assert.Equal(LearnerProfileSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
            Assert.Equal(0.60, snapshot.DefaultUnderstanding, 12);
            Assert.Equal(new[] { "apple", "keep off" }, snapshot.Entries.Select(entry => entry.Text).ToArray());
            Assert.Equal(new[] { 0.40, 0.90 }, snapshot.Entries.Select(entry => entry.Understanding).ToArray());
        }

        [Fact]
        public void SnapshotRejectsDuplicateNormalizedKeys()
        {
            var error = Assert.Throws<ArgumentException>(() =>
                new LearnerProfileSnapshot(
                    0.55,
                    new[]
                    {
                        new LearnerKnowledgeEntry("Keep off", 0.20),
                        new LearnerKnowledgeEntry(" keep   OFF ", 0.80),
                    }));

            Assert.Contains("duplicate normalized key", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(-0.01)]
        [InlineData(1.01)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void PersistenceEntriesRejectCorruptUnderstanding(double value)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LearnerKnowledgeEntry("keep off", value));
        }

        [Fact]
        public void SnapshotRejectsUnknownSchemaVersion()
        {
            var error = Assert.Throws<NotSupportedException>(() =>
                new LearnerProfileSnapshot(
                    LearnerProfileSnapshot.CurrentSchemaVersion + 1,
                    0.55,
                    Array.Empty<LearnerKnowledgeEntry>()));

            Assert.Contains("schema version", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void InMemoryModelRoundTripsSnapshotAndReplacesOldState()
        {
            var source = new InMemoryLearnerModel(0.30);
            source.SetUnderstanding("keep off", 0.91);
            var snapshot = source.CreateSnapshot();

            var restored = new InMemoryLearnerModel(0.80);
            restored.SetUnderstanding("stale phrase", 0.10);
            restored.LoadSnapshot(snapshot);

            Assert.Equal(0.30, restored.DefaultUnderstanding, 12);
            Assert.Equal(1, restored.ExplicitEntryCount);

            var known = restored.Estimate(Unit("keep off"));
            Assert.True(known.IsExplicit);
            Assert.Equal(0.91, known.Understanding, 12);

            var stale = restored.Estimate(Unit("stale phrase"));
            Assert.False(stale.IsExplicit);
            Assert.Equal(0.30, stale.Understanding, 12);
        }

        [Fact]
        public void PersistentModelLoadsExistingProfileWithoutWritingItBack()
        {
            var store = new RecordingStore
            {
                Loaded = new LearnerProfileSnapshot(
                    0.25,
                    new[] { new LearnerKnowledgeEntry("keep off", 0.88) }),
            };

            var model = new PersistentLearnerModel(store, fallbackDefaultUnderstanding: 0.75);
            var estimate = model.Estimate(Unit("keep off"));

            Assert.Equal(1, store.LoadCount);
            Assert.Equal(0, store.SaveCount);
            Assert.True(estimate.IsExplicit);
            Assert.Equal(0.88, estimate.Understanding, 12);
            Assert.Equal(0.25, model.CreateSnapshot().DefaultUnderstanding, 12);
        }

        [Fact]
        public void PersistentMutationSavesOneNormalizedSnapshot()
        {
            var store = new RecordingStore();
            var model = new PersistentLearnerModel(store, fallbackDefaultUnderstanding: 0.55);

            model.SetUnderstanding("  Keep   OFF ", 0.93);

            Assert.Equal(1, store.SaveCount);
            var saved = Assert.IsType<LearnerProfileSnapshot>(store.Saved);
            var entry = Assert.Single(saved.Entries);
            Assert.Equal("keep off", entry.Text);
            Assert.Equal(0.93, entry.Understanding, 12);
        }

        [Fact]
        public void ExplicitSnapshotLoadIsPersistedAndReplacesState()
        {
            var store = new RecordingStore();
            var model = new PersistentLearnerModel(store, fallbackDefaultUnderstanding: 0.60);
            model.SetUnderstanding("obsolete", 0.20);
            store.ResetSaves();

            model.LoadSnapshot(new LearnerProfileSnapshot(
                0.35,
                new[] { new LearnerKnowledgeEntry("in spite of", 0.77) }));

            Assert.Equal(1, store.SaveCount);
            Assert.False(model.Estimate(Unit("obsolete")).IsExplicit);
            Assert.Equal(0.35, model.Estimate(Unit("obsolete")).Understanding, 12);
            Assert.Equal(0.77, model.Estimate(Unit("in spite of")).Understanding, 12);
        }

        private static SemanticUnit Unit(string text)
        {
            var document = new RuleBasedSemanticSegmenter(new[] { text }).Segment(text);
            return document.OfKind(SemanticUnitKind.MultiwordExpression).Single();
        }

        private sealed class RecordingStore : ILearnerProfileStore
        {
            public LearnerProfileSnapshot? Loaded { get; set; }
            public LearnerProfileSnapshot? Saved { get; private set; }
            public int LoadCount { get; private set; }
            public int SaveCount { get; private set; }

            public LearnerProfileSnapshot? Load()
            {
                LoadCount++;
                return Loaded;
            }

            public void Save(LearnerProfileSnapshot snapshot)
            {
                Saved = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
                SaveCount++;
            }

            public void ResetSaves()
            {
                Saved = null;
                SaveCount = 0;
            }
        }
    }
}
