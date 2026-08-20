using System;
using System.Collections.Generic;

namespace PhraseLayer.Core.Learning
{
    /// <summary>
    /// A normalized learner-knowledge entry suitable for storage.
    /// Persistence is deliberately stricter than SetUnderstanding: corrupt/non-finite values are rejected
    /// instead of silently clamped while loading a profile.
    /// </summary>
    public sealed class LearnerKnowledgeEntry
    {
        public LearnerKnowledgeEntry(string text, double understanding)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Learner knowledge text is required.", nameof(text));
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

    /// <summary>
    /// Versioned, platform-neutral snapshot of the learner state.
    /// It contains normalized language knowledge only; filesystem/JSON concerns stay outside Core.
    /// </summary>
    public sealed class LearnerProfileSnapshot
    {
        public const int CurrentSchemaVersion = 1;
        private readonly LearnerKnowledgeEntry[] entries;

        public LearnerProfileSnapshot(
            double defaultUnderstanding,
            IEnumerable<LearnerKnowledgeEntry> entries)
            : this(CurrentSchemaVersion, defaultUnderstanding, entries)
        {
        }

        public LearnerProfileSnapshot(
            int schemaVersion,
            double defaultUnderstanding,
            IEnumerable<LearnerKnowledgeEntry> entries)
        {
            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new NotSupportedException(
                    "Unsupported learner profile schema version " + schemaVersion +
                    "; expected " + CurrentSchemaVersion + ".");
            }
            LearnerKnowledgeEntry.ValidateUnderstanding(defaultUnderstanding, nameof(defaultUnderstanding));
            if (entries == null) throw new ArgumentNullException(nameof(entries));

            var copy = new List<LearnerKnowledgeEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry == null)
                    throw new ArgumentException("Learner profile entries cannot contain null values.", nameof(entries));

                // Reconstruct each entry so externally supplied subclasses/mutable collections cannot bypass
                // normalization and score validation, then reject duplicate normalized keys deterministically.
                var normalized = new LearnerKnowledgeEntry(entry.Text, entry.Understanding);
                if (!seen.Add(normalized.Text))
                {
                    throw new ArgumentException(
                        "Learner profile contains duplicate normalized key: " + normalized.Text,
                        nameof(entries));
                }
                copy.Add(normalized);
            }

            copy.Sort((left, right) => StringComparer.Ordinal.Compare(left.Text, right.Text));
            SchemaVersion = schemaVersion;
            DefaultUnderstanding = defaultUnderstanding;
            this.entries = copy.ToArray();
        }

        public int SchemaVersion { get; }
        public double DefaultUnderstanding { get; }
        public IReadOnlyList<LearnerKnowledgeEntry> Entries => entries;
    }

    /// <summary>
    /// Mutable learner contract used by adaptive assistance and persistence layers.
    /// Existing ILearnerModel remains read-only for consumers that only need estimation.
    /// </summary>
    public interface IMutableLearnerModel : ILearnerModel
    {
        void SetUnderstanding(string text, double understanding);
        LearnerProfileSnapshot CreateSnapshot();
        void LoadSnapshot(LearnerProfileSnapshot snapshot);
    }

    /// <summary>
    /// Storage boundary. Implementations may use Unity persistentDataPath, a database, or an in-memory test store.
    /// Serialization and filesystem APIs intentionally do not leak into PhraseLayer.Core.
    /// </summary>
    public interface ILearnerProfileStore
    {
        LearnerProfileSnapshot? Load();
        void Save(LearnerProfileSnapshot snapshot);
    }

    /// <summary>
    /// Auto-persisting learner model. Each explicit mutation is saved after the in-memory state changes.
    /// No learning-rate/update heuristic is embedded here; callers decide the score they want to persist.
    /// </summary>
    public sealed class PersistentLearnerModel : IMutableLearnerModel
    {
        private readonly ILearnerProfileStore store;
        private readonly InMemoryLearnerModel inner;

        public PersistentLearnerModel(
            ILearnerProfileStore store,
            double fallbackDefaultUnderstanding = 0.55)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            LearnerKnowledgeEntry.ValidateUnderstanding(
                fallbackDefaultUnderstanding,
                nameof(fallbackDefaultUnderstanding));

            var existing = store.Load();
            inner = existing == null
                ? new InMemoryLearnerModel(fallbackDefaultUnderstanding)
                : InMemoryLearnerModel.FromSnapshot(existing);
        }

        public KnowledgeEstimate Estimate(Semantics.SemanticUnit unit) => inner.Estimate(unit);

        public void SetUnderstanding(string text, double understanding)
        {
            inner.SetUnderstanding(text, understanding);
            store.Save(inner.CreateSnapshot());
        }

        public LearnerProfileSnapshot CreateSnapshot() => inner.CreateSnapshot();

        public void LoadSnapshot(LearnerProfileSnapshot snapshot)
        {
            inner.LoadSnapshot(snapshot);
            store.Save(inner.CreateSnapshot());
        }
    }
}
