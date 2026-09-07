using System;
using System.Collections.Generic;
using PhraseLayer.Core.Semantics;

namespace PhraseLayer.Core.Learning
{
    /// <summary>A normalized immutable learner entry; stored scores must be finite and within [0,1].</summary>
    public sealed class LearnerKnowledgeEntry
    {
        public LearnerKnowledgeEntry(string text, double understanding)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Learner knowledge text is required.", nameof(text));
            ValidateUnderstanding(understanding, nameof(understanding));
            Text = InMemoryLearnerModel.Normalize(text);
            Understanding = understanding;
        }
        public string Text { get; }
        public double Understanding { get; }
        internal static void ValidateUnderstanding(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(parameterName, "Understanding must be finite and within [0,1].");
        }
    }
    /// <summary>Immutable, versioned, platform-neutral learner state. Filesystem and JSON stay outside Core.</summary>
    public sealed class LearnerProfileSnapshot
    {
        public const int CurrentSchemaVersion = 1;
        private readonly IReadOnlyList<LearnerKnowledgeEntry> entries;
        public LearnerProfileSnapshot(double defaultUnderstanding, IEnumerable<LearnerKnowledgeEntry> entries)
            : this(CurrentSchemaVersion, defaultUnderstanding, entries) { }
        public LearnerProfileSnapshot(int schemaVersion, double defaultUnderstanding, IEnumerable<LearnerKnowledgeEntry> entries)
        {
            if (schemaVersion != CurrentSchemaVersion)
                throw new NotSupportedException("Unsupported learner profile schema version " + schemaVersion + "; expected " + CurrentSchemaVersion + ".");
            LearnerKnowledgeEntry.ValidateUnderstanding(defaultUnderstanding, nameof(defaultUnderstanding));
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            var copy = new List<LearnerKnowledgeEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry == null) throw new ArgumentException("Learner profile entries cannot contain null values.", nameof(entries));
                var normalized = new LearnerKnowledgeEntry(entry.Text, entry.Understanding);
                if (!seen.Add(normalized.Text)) throw new ArgumentException("Learner profile contains duplicate normalized key: " + normalized.Text, nameof(entries));
                copy.Add(normalized);
            }
            copy.Sort((left, right) => StringComparer.Ordinal.Compare(left.Text, right.Text));
            SchemaVersion = schemaVersion;
            DefaultUnderstanding = defaultUnderstanding;
            this.entries = Array.AsReadOnly(copy.ToArray());
        }
        public int SchemaVersion { get; }
        public double DefaultUnderstanding { get; }
        public IReadOnlyList<LearnerKnowledgeEntry> Entries => entries;
    }
    public interface IMutableLearnerModel : ILearnerModel
    {
        void SetUnderstanding(string text, double understanding);
        LearnerProfileSnapshot CreateSnapshot();
        void LoadSnapshot(LearnerProfileSnapshot snapshot);
    }
    /// <summary>
    /// Save replaces a complete snapshot. A throw may have an unknown durable outcome; callers retry the same
    /// prepared snapshot rather than recomputing learning. This interface alone does not establish crash durability.
    /// </summary>
    public interface ILearnerProfileStore
    {
        LearnerProfileSnapshot? Load();
        void Save(LearnerProfileSnapshot snapshot);
    }
    /// <summary>
    /// Single-owner auto-persisting model. Validate and stage first, save once, publish in-memory state only
    /// after Save returns. Storage exceptions do not publish a partial in-memory mutation.
    /// </summary>
    public sealed class PersistentLearnerModel : IMutableLearnerModel
    {
        private readonly ILearnerProfileStore store;
        private readonly InMemoryLearnerModel inner;
        public PersistentLearnerModel(ILearnerProfileStore store, double fallbackDefaultUnderstanding = 0.55)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            LearnerKnowledgeEntry.ValidateUnderstanding(fallbackDefaultUnderstanding, nameof(fallbackDefaultUnderstanding));
            var existing = store.Load();
            inner = existing == null ? new InMemoryLearnerModel(fallbackDefaultUnderstanding) : InMemoryLearnerModel.FromSnapshot(existing);
        }
        public KnowledgeEstimate Estimate(SemanticUnit unit) => inner.Estimate(unit);
        public void SetUnderstanding(string text, double understanding)
        {
            var staged = InMemoryLearnerModel.FromSnapshot(inner.CreateSnapshot());
            staged.SetUnderstanding(text, understanding);
            PersistThenPublish(staged.CreateSnapshot());
        }
        public LearnerProfileSnapshot CreateSnapshot() => inner.CreateSnapshot();
        public void LoadSnapshot(LearnerProfileSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var validated = new LearnerProfileSnapshot(snapshot.SchemaVersion, snapshot.DefaultUnderstanding, snapshot.Entries);
            PersistThenPublish(validated);
        }
        private void PersistThenPublish(LearnerProfileSnapshot snapshot)
        {
            store.Save(snapshot);
            inner.LoadSnapshot(snapshot);
        }
    }
}
