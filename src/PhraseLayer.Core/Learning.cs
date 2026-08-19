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

    public sealed class InMemoryLearnerModel : ILearnerModel
    {
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private readonly Dictionary<string, double> _understanding = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly double _defaultUnderstanding;
        public InMemoryLearnerModel(double defaultUnderstanding = 0.55) { _defaultUnderstanding = Clamp01(defaultUnderstanding); }
        public void SetUnderstanding(string text, double understanding)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is required.", nameof(text));
            _understanding[Normalize(text)] = Clamp01(understanding);
        }
        public KnowledgeEstimate Estimate(SemanticUnit unit)
        {
            double score;
            if (_understanding.TryGetValue(Normalize(unit.Text), out score)) return new KnowledgeEstimate(unit, score, true);
            return new KnowledgeEstimate(unit, _defaultUnderstanding, false);
        }
        public static string Normalize(string value) => Whitespace.Replace(value.Trim().ToLowerInvariant(), " ");
        private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
    }
}
