using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PhraseLayer.Core.Semantics
{
    public enum SemanticUnitKind { Word = 0, MultiwordExpression = 1, Phrase = 2, Clause = 3, Sentence = 4 }

    public sealed class SemanticUnit
    {
        public SemanticUnit(string id, SemanticUnitKind kind, int start, int length, string text, int tokenCount)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Unit id is required.", nameof(id));
            if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            if (tokenCount <= 0) throw new ArgumentOutOfRangeException(nameof(tokenCount));
            Id = id; Kind = kind; Start = start; Length = length;
            Text = text ?? throw new ArgumentNullException(nameof(text));
            TokenCount = tokenCount;
        }
        public string Id { get; }
        public SemanticUnitKind Kind { get; }
        public int Start { get; }
        public int Length { get; }
        public int End => Start + Length;
        public string Text { get; }
        public int TokenCount { get; }
        public bool Contains(SemanticUnit other) => Start <= other.Start && End >= other.End;
        public bool Overlaps(SemanticUnit other) => Start < other.End && other.Start < End;
    }

    public sealed class SemanticDocument
    {
        private readonly IReadOnlyList<SemanticUnit> _units;
        public SemanticDocument(string sourceText, IEnumerable<SemanticUnit> units)
        {
            SourceText = sourceText ?? throw new ArgumentNullException(nameof(sourceText));
            _units = Array.AsReadOnly((units ?? throw new ArgumentNullException(nameof(units)))
                .OrderBy(unit => unit.Start).ThenByDescending(unit => unit.Length).ToArray());
        }
        public string SourceText { get; }
        public IReadOnlyList<SemanticUnit> Units => _units;
        public IEnumerable<SemanticUnit> OfKind(SemanticUnitKind kind) => _units.Where(unit => unit.Kind == kind);
    }

    public interface ISemanticSegmenter { SemanticDocument Segment(string sourceText); }

    public sealed class RuleBasedSemanticSegmenter : ISemanticSegmenter
    {
        private static readonly Regex WordRegex = new Regex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private readonly string[] _multiwordExpressions;
        private readonly Regex[] _expressionPatterns;

        public RuleBasedSemanticSegmenter(IEnumerable<string>? multiwordExpressions = null)
        {
            _multiwordExpressions = (multiwordExpressions ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => WhitespaceRegex.Replace(value.Trim(), " "))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(TokenCount).ThenByDescending(value => value.Length).ToArray();
            _expressionPatterns = _multiwordExpressions.Select(expression => new Regex(
                string.Join(@"\s+", expression.Split(' ').Select(Regex.Escape)),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)).ToArray();
        }

        public SemanticDocument Segment(string sourceText)
        {
            if (sourceText == null) throw new ArgumentNullException(nameof(sourceText));
            var units = new List<SemanticUnit>();
            var sentence = TrimSpan(sourceText, 0, sourceText.Length);
            if (sentence.Length == 0) return new SemanticDocument(sourceText, units);
            var words = WordRegex.Matches(sourceText).Cast<Match>().ToArray();
            units.Add(CreateUnit(SemanticUnitKind.Sentence, sentence.Start, sentence.Length, sourceText));
            foreach (var clause in SplitClauses(sourceText, sentence.Start, sentence.Length))
                units.Add(CreateUnit(SemanticUnitKind.Clause, clause.Start, clause.Length, sourceText));
            units.AddRange(FindMultiwordExpressions(sourceText, words));
            foreach (var match in words)
                units.Add(new SemanticUnit(MakeId(SemanticUnitKind.Word, match.Index, match.Length), SemanticUnitKind.Word,
                    match.Index, match.Length, match.Value, 1));
            return new SemanticDocument(sourceText, units);
        }

        private IEnumerable<SemanticUnit> FindMultiwordExpressions(string sourceText, IReadOnlyList<Match> words)
        {
            var accepted = new List<SemanticUnit>();
            for (var expressionIndex = 0; expressionIndex < _multiwordExpressions.Length; expressionIndex++)
            {
                foreach (Match match in _expressionPatterns[expressionIndex].Matches(sourceText))
                {
                    // Match source whitespace without rewriting source offsets. Both ends must respect the same
                    // lexical tokenization used by the atomic word layer (including apostrophes and hyphens).
                    if (SplitsAtomicWord(match.Index, match.Index + match.Length, words)) continue;
                    var candidate = new SemanticUnit(MakeId(SemanticUnitKind.MultiwordExpression, match.Index, match.Length),
                        SemanticUnitKind.MultiwordExpression, match.Index, match.Length,
                        sourceText.Substring(match.Index, match.Length), TokenCount(_multiwordExpressions[expressionIndex]));
                    if (!accepted.Any(existing => existing.Overlaps(candidate))) accepted.Add(candidate);
                }
            }
            return accepted;
        }

        private static bool SplitsAtomicWord(int start, int end, IReadOnlyList<Match> words)
        {
            foreach (var word in words)
            {
                if (word.Index >= end) break;
                var wordEnd = word.Index + word.Length;
                if ((word.Index < start && start < wordEnd) || (word.Index < end && end < wordEnd)) return true;
            }
            return false;
        }

        private static IEnumerable<TextSpan> SplitClauses(string sourceText, int start, int length)
        {
            var end = start + length;
            var segmentStart = start;
            for (var index = start; index < end; index++)
            {
                if (sourceText[index] == ',' || sourceText[index] == ';' || sourceText[index] == ':')
                {
                    var span = TrimSpan(sourceText, segmentStart, index - segmentStart);
                    if (span.Length > 0) yield return span;
                    segmentStart = index + 1;
                }
            }
            var last = TrimSpan(sourceText, segmentStart, end - segmentStart);
            last = TrimTerminalPunctuation(sourceText, last.Start, last.Length);
            if (last.Length > 0) yield return last;
        }
        private static TextSpan TrimSpan(string sourceText, int start, int length)
        {
            var left = start; var right = start + length;
            while (left < right && char.IsWhiteSpace(sourceText[left])) left++;
            while (right > left && char.IsWhiteSpace(sourceText[right - 1])) right--;
            return new TextSpan(left, right - left);
        }
        private static TextSpan TrimTerminalPunctuation(string sourceText, int start, int length)
        {
            var right = start + length;
            while (right > start && (sourceText[right - 1] == '.' || sourceText[right - 1] == '!' || sourceText[right - 1] == '?')) right--;
            return TrimSpan(sourceText, start, right - start);
        }
        private static SemanticUnit CreateUnit(SemanticUnitKind kind, int start, int length, string sourceText)
        {
            var text = sourceText.Substring(start, length);
            return new SemanticUnit(MakeId(kind, start, length), kind, start, length, text, Math.Max(1, TokenCount(text)));
        }
        private static int TokenCount(string text) => WordRegex.Matches(text).Count;
        private static string MakeId(SemanticUnitKind kind, int start, int length) => kind + ":" + start + ":" + length;
        private readonly struct TextSpan
        {
            public TextSpan(int start, int length) { Start = start; Length = length; }
            public int Start { get; }
            public int Length { get; }
        }
    }
}
