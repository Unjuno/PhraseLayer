using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PhraseLayer.Core.Semantics;

namespace PhraseLayer.Core.Learning
{
    public enum KnowledgeState { Unknown = 0, Learning = 1, Known = 2 }

    public sealed class KnowledgeEstimate
    {
        public KnowledgeEstimate(SemanticUnit unit, double understanding, bool isExplicit)
        {
            Unit = unit;
            Understanding = Clamp01(understanding);
            IsExplicit = isExplicit;
            State = Understanding >= 0.82 ? KnowledgeState.Known : Understanding >= 0.45 ? KnowledgeState.Learning : KnowledgeState.Unknown;
        }
        public SemanticUnit Unit { get; }
        public double Understanding { get; }
        public bool IsExplicit { get; }
        public KnowledgeState State { get; }
        private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
    }

    public interface ILearnerModel { KnowledgeEstimate Estimate(SemanticUnit unit); }

    public sealed class InMemoryLearnerModel : IMutableLearnerModel
    {
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private readonly Dictionary<string, double> _understanding = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private double _defaultUnderstanding;

        public InMemoryLearnerModel(double defaultUnderstanding = 0.55)
        {
            _defaultUnderstanding = Clamp01(defaultUnderstanding);
        }

        public double DefaultUnderstanding => _defaultUnderstanding;
        public int ExplicitEntryCount => _understanding.Count;

        public void SetUnderstanding(string text, double understanding)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is required.", nameof(text));
            _understanding[Normalize(text)] = Clamp01(understanding);
        }

        public KnowledgeEstimate Estimate(SemanticUnit unit)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            double score;
            if (_understanding.TryGetValue(Normalize(unit.Text), out score)) return new KnowledgeEstimate(unit, score, true);
            return new KnowledgeEstimate(unit, _defaultUnderstanding, false);
        }

        public LearnerProfileSnapshot CreateSnapshot()
        {
            var entries = new List<LearnerKnowledgeEntry>(_understanding.Count);
            foreach (var pair in _understanding)
                entries.Add(new LearnerKnowledgeEntry(pair.Key, pair.Value));
            return new LearnerProfileSnapshot(_defaultUnderstanding, entries);
        }

        public void LoadSnapshot(LearnerProfileSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            _understanding.Clear();
            _defaultUnderstanding = snapshot.DefaultUnderstanding;
            foreach (var entry in snapshot.Entries)
                _understanding.Add(entry.Text, entry.Understanding);
        }

        public static InMemoryLearnerModel FromSnapshot(LearnerProfileSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var model = new InMemoryLearnerModel(snapshot.DefaultUnderstanding);
            model.LoadSnapshot(snapshot);
            return model;
        }

        public static string Normalize(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return Whitespace.Replace(value.Trim().ToLowerInvariant(), " ");
        }

        private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
    }
}
