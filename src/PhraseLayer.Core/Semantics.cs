using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PhraseLayer.Core.Semantics
{
    public enum SemanticUnitKind
    {
        Word = 0,
        MultiwordExpression = 1,
        Phrase = 2,
        Clause = 3,
        Sentence = 4
    }

    public sealed class SemanticUnit
    {
        public SemanticUnit(string id, SemanticUnitKind kind, int start, int length, string text, int tokenCount)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Unit id is required.", nameof(id));
            if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            if (tokenCount <= 0) throw new ArgumentOutOfRangeException(nameof(tokenCount));
            Id = id;
            Kind = kind;
            Start = start;
            Length = length;
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
            _units = (units ?? throw new ArgumentNullException(nameof(units)))
                .OrderBy(unit => unit.Start).ThenByDescending(unit => unit.Length).ToArray();
        }
        public string SourceText { get; }
        public IReadOnlyList<SemanticUnit> Units => _units;
        public IEnumerable<SemanticUnit> OfKind(SemanticUnitKind kind) => _units.Where(unit => unit.Kind == kind);
    }

    public interface ISemanticSegmenter
    {
        SemanticDocument Segment(string sourceText);
    }

    /// <summary>
    /// Deterministic bootstrap segmenter. Configured phrase patterns are an explicit lexicon, not a claim of
    /// general syntactic parsing; a later parser can replace this implementation through ISemanticSegmenter.
    /// </summary>
    public sealed class RuleBasedSemanticSegmenter : ISemanticSegmenter
    {
        private static readonly Regex WordRegex = new Regex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*", RegexOptions.Compiled);
        private readonly string[] _multiwordExpressions;
        private readonly string[] _phrasePatterns;

        public RuleBasedSemanticSegmenter(
            IEnumerable<string>? multiwordExpressions = null,
            IEnumerable<string>? phrasePatterns = null)
        {
            _multiwordExpressions = NormalizePatterns(multiwordExpressions);
            _phrasePatterns = NormalizePatterns(phrasePatterns);
        }

        public SemanticDocument Segment(string sourceText)
        {
            if (sourceText == null) throw new ArgumentNullException(nameof(sourceText));
            var units = new List<SemanticUnit>();
            var documentSpan = TrimSpan(sourceText, 0, sourceText.Length);
            if (documentSpan.Length == 0) return new SemanticDocument(sourceText, units);

            foreach (var sentence in SplitSentences(sourceText, documentSpan.Start, documentSpan.Length))
            {
                units.Add(CreateUnit(SemanticUnitKind.Sentence, sentence.Start, sentence.Length, sourceText));
                foreach (var clause in SplitClauses(sourceText, sentence.Start, sentence.Length))
                    units.Add(CreateUnit(SemanticUnitKind.Clause, clause.Start, clause.Length, sourceText));
            }

            var clauses = units.Where(unit => unit.Kind == SemanticUnitKind.Clause).ToArray();
            units.AddRange(FindConfiguredSpans(sourceText, _phrasePatterns, SemanticUnitKind.Phrase, clauses));
            units.AddRange(FindConfiguredSpans(sourceText, _multiwordExpressions, SemanticUnitKind.MultiwordExpression, clauses));
            foreach (Match match in WordRegex.Matches(sourceText))
                units.Add(new SemanticUnit(MakeId(SemanticUnitKind.Word, match.Index, match.Length), SemanticUnitKind.Word, match.Index, match.Length, match.Value, 1));

            return new SemanticDocument(sourceText, units);
        }

        private static string[] NormalizePatterns(IEnumerable<string>? patterns)
        {
            return (patterns ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(TokenCount)
                .ThenByDescending(value => value.Length)
                .ToArray();
        }

        private static IEnumerable<SemanticUnit> FindConfiguredSpans(
            string sourceText,
            IReadOnlyList<string> patterns,
            SemanticUnitKind kind,
            IReadOnlyList<SemanticUnit> clauseContainers)
        {
            var accepted = new List<SemanticUnit>();
            foreach (var pattern in patterns)
            {
                var searchStart = 0;
                while (searchStart < sourceText.Length)
                {
                    var index = sourceText.IndexOf(pattern, searchStart, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;
                    var end = index + pattern.Length;
                    if (IsBoundary(sourceText, index - 1) && IsBoundary(sourceText, end))
                    {
                        var candidate = new SemanticUnit(
                            MakeId(kind, index, pattern.Length),
                            kind,
                            index,
                            pattern.Length,
                            sourceText.Substring(index, pattern.Length),
                            TokenCount(pattern));
                        if (clauseContainers.Any(clause => clause.Contains(candidate)) &&
                            !accepted.Any(existing => existing.Overlaps(candidate)))
                        {
                            accepted.Add(candidate);
                        }
                    }
                    searchStart = index + Math.Max(1, pattern.Length);
                }
            }
            return accepted;
        }

        private static IEnumerable<TextSpan> SplitSentences(string sourceText, int start, int length)
        {
            var end = start + length;
            var sentenceStart = start;
            var index = start;

            while (index < end)
            {
                if (!IsSentenceTerminator(sourceText[index]) || IsDecimalPoint(sourceText, index, start, end))
                {
                    index++;
                    continue;
                }

                var boundaryEnd = index + 1;
                while (boundaryEnd < end && IsSentenceTerminator(sourceText[boundaryEnd])) boundaryEnd++;
                while (boundaryEnd < end && IsSentenceClosingMark(sourceText[boundaryEnd])) boundaryEnd++;

                var span = TrimSpan(sourceText, sentenceStart, boundaryEnd - sentenceStart);
                if (span.Length > 0) yield return span;

                sentenceStart = boundaryEnd;
                while (sentenceStart < end && char.IsWhiteSpace(sourceText[sentenceStart])) sentenceStart++;
                index = sentenceStart;
            }

            var last = TrimSpan(sourceText, sentenceStart, end - sentenceStart);
            if (last.Length > 0) yield return last;
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
            var left = start;
            var right = start + length;
            while (left < right && char.IsWhiteSpace(sourceText[left])) left++;
            while (right > left && char.IsWhiteSpace(sourceText[right - 1])) right--;
            return new TextSpan(left, right - left);
        }

        private static TextSpan TrimTerminalPunctuation(string sourceText, int start, int length)
        {
            var right = start + length;
            while (right > start && IsSentenceClosingMark(sourceText[right - 1])) right--;
            while (right > start && IsSentenceTerminator(sourceText[right - 1])) right--;
            return TrimSpan(sourceText, start, right - start);
        }

        private static SemanticUnit CreateUnit(SemanticUnitKind kind, int start, int length, string sourceText)
        {
            var text = sourceText.Substring(start, length);
            return new SemanticUnit(MakeId(kind, start, length), kind, start, length, text, Math.Max(1, TokenCount(text)));
        }

        private static bool IsBoundary(string text, int index) => index < 0 || index >= text.Length || !char.IsLetterOrDigit(text[index]);
        private static bool IsSentenceTerminator(char value) => value == '.' || value == '!' || value == '?';
        private static bool IsSentenceClosingMark(char value) =>
            value == '"' || value == '\'' || value == '’' || value == '”' || value == ')' || value == ']' || value == '}';
        private static bool IsDecimalPoint(string text, int index, int start, int end) =>
            text[index] == '.' && index > start && index + 1 < end && char.IsDigit(text[index - 1]) && char.IsDigit(text[index + 1]);
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
