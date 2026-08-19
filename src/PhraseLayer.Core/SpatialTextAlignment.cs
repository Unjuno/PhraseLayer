using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Pipeline;

namespace PhraseLayer.Core.Spatial
{
    public enum OcrTextMatchKind
    {
        ExactTokenSequence = 0,
        Unresolved = 1
    }

    public sealed class OcrTextRegionSpan
    {
        public OcrTextRegionSpan(OcrViewportRegion region, int sourceStart, int sourceLength, OcrTextMatchKind matchKind)
        {
            Region = region ?? throw new ArgumentNullException(nameof(region));
            if (sourceStart < -1) throw new ArgumentOutOfRangeException(nameof(sourceStart));
            if (sourceLength < 0) throw new ArgumentOutOfRangeException(nameof(sourceLength));
            SourceStart = sourceStart;
            SourceLength = sourceLength;
            MatchKind = matchKind;
        }

        public OcrViewportRegion Region { get; }
        public int SourceStart { get; }
        public int SourceLength { get; }
        public int SourceEnd => SourceStart < 0 ? -1 : SourceStart + SourceLength;
        public OcrTextMatchKind MatchKind { get; }
        public bool IsResolved => SourceStart >= 0 && MatchKind != OcrTextMatchKind.Unresolved;
        public bool Overlaps(int start, int end) => IsResolved && SourceStart < end && start < SourceEnd;
    }

    public sealed class OcrTextAlignmentResult
    {
        public OcrTextAlignmentResult(string sourceText, IReadOnlyList<OcrTextRegionSpan> regions)
        {
            SourceText = sourceText ?? throw new ArgumentNullException(nameof(sourceText));
            Regions = regions ?? throw new ArgumentNullException(nameof(regions));
        }

        public string SourceText { get; }
        public IReadOnlyList<OcrTextRegionSpan> Regions { get; }
        public IReadOnlyList<OcrTextRegionSpan> ResolvedRegions => Regions.Where(item => item.IsResolved).ToArray();
        public IReadOnlyList<OcrTextRegionSpan> UnresolvedRegions => Regions.Where(item => !item.IsResolved).ToArray();
    }

    public sealed class OcrRegionTextAligner
    {
        private static readonly Regex TokenRegex = new Regex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*", RegexOptions.Compiled);

        public OcrTextAlignmentResult Align(OcrObservation observation, IReadOnlyList<OcrViewportRegion> viewportRegions)
        {
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            if (viewportRegions == null) throw new ArgumentNullException(nameof(viewportRegions));

            var sourceTokens = Tokenize(observation.Text);
            var claimed = new bool[sourceTokens.Count];
            var cursor = 0;
            var output = new List<OcrTextRegionSpan>(viewportRegions.Count);

            foreach (var viewportRegion in viewportRegions)
            {
                var regionTokens = Tokenize(viewportRegion.Source.Text);
                var match = FindMatch(sourceTokens, regionTokens, claimed, cursor);
                if (match.StartToken < 0)
                {
                    output.Add(new OcrTextRegionSpan(viewportRegion, -1, 0, OcrTextMatchKind.Unresolved));
                    continue;
                }

                for (var index = match.StartToken; index <= match.EndToken; index++) claimed[index] = true;
                cursor = match.EndToken + 1;
                var start = sourceTokens[match.StartToken].Start;
                var end = sourceTokens[match.EndToken].End;
                output.Add(new OcrTextRegionSpan(
                    viewportRegion,
                    start,
                    end - start,
                    OcrTextMatchKind.ExactTokenSequence));
            }

            return new OcrTextAlignmentResult(observation.Text, output);
        }

        private static TokenMatch FindMatch(
            IReadOnlyList<TokenSpan> sourceTokens,
            IReadOnlyList<TokenSpan> regionTokens,
            IReadOnlyList<bool> claimed,
            int cursor)
        {
            if (regionTokens.Count == 0 || sourceTokens.Count < regionTokens.Count)
                return TokenMatch.None;

            var afterCursor = FindFirst(sourceTokens, regionTokens, claimed, Math.Max(0, cursor));
            if (afterCursor.StartToken >= 0) return afterCursor;
            return FindFirst(sourceTokens, regionTokens, claimed, 0);
        }

        private static TokenMatch FindFirst(
            IReadOnlyList<TokenSpan> sourceTokens,
            IReadOnlyList<TokenSpan> regionTokens,
            IReadOnlyList<bool> claimed,
            int firstSourceToken)
        {
            var maxStart = sourceTokens.Count - regionTokens.Count;
            for (var start = firstSourceToken; start <= maxStart; start++)
            {
                var matches = true;
                for (var offset = 0; offset < regionTokens.Count; offset++)
                {
                    if (claimed[start + offset] ||
                        !string.Equals(sourceTokens[start + offset].Normalized, regionTokens[offset].Normalized, StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches) return new TokenMatch(start, start + regionTokens.Count - 1);
            }
            return TokenMatch.None;
        }

        private static IReadOnlyList<TokenSpan> Tokenize(string text)
        {
            var tokens = new List<TokenSpan>();
            foreach (Match match in TokenRegex.Matches(text ?? string.Empty))
                tokens.Add(new TokenSpan(match.Index, match.Length, match.Value.ToLowerInvariant()));
            return tokens;
        }

        private readonly struct TokenSpan
        {
            public TokenSpan(int start, int length, string normalized)
            { Start = start; Length = length; Normalized = normalized; }
            public int Start { get; }
            public int Length { get; }
            public int End => Start + Length;
            public string Normalized { get; }
        }

        private readonly struct TokenMatch
        {
            public static TokenMatch None => new TokenMatch(-1, -1);
            public TokenMatch(int startToken, int endToken) { StartToken = startToken; EndToken = endToken; }
            public int StartToken { get; }
            public int EndToken { get; }
        }
    }

    public enum SpatialAssistanceCoverage
    {
        Unresolved = 0,
        Partial = 1,
        Exact = 2
    }

    public readonly struct ViewportEnvelope
    {
        public ViewportEnvelope(double minU, double minV, double maxU, double maxV)
        {
            MinU = Math.Max(0.0, Math.Min(1.0, minU));
            MinV = Math.Max(0.0, Math.Min(1.0, minV));
            MaxU = Math.Max(MinU, Math.Max(0.0, Math.Min(1.0, maxU)));
            MaxV = Math.Max(MinV, Math.Max(0.0, Math.Min(1.0, maxV)));
        }

        public double MinU { get; }
        public double MinV { get; }
        public double MaxU { get; }
        public double MaxV { get; }
        public ViewportPoint Center => new ViewportPoint((MinU + MaxU) / 2.0, (MinV + MaxV) / 2.0);

        public static ViewportEnvelope FromRegions(IReadOnlyList<OcrTextRegionSpan> regions)
        {
            if (regions == null) throw new ArgumentNullException(nameof(regions));
            if (regions.Count == 0) throw new ArgumentException("At least one region is required.", nameof(regions));

            var points = regions.SelectMany(region => region.Region.ViewportBounds.Points).ToArray();
            return new ViewportEnvelope(
                points.Min(point => point.U),
                points.Min(point => point.V),
                points.Max(point => point.U),
                points.Max(point => point.V));
        }
    }

    public sealed class SpatialAssistanceTarget
    {
        public SpatialAssistanceTarget(
            MixedLanguageSegment segment,
            IReadOnlyList<OcrTextRegionSpan> regions,
            SpatialAssistanceCoverage coverage,
            ViewportEnvelope? envelope)
        {
            Segment = segment ?? throw new ArgumentNullException(nameof(segment));
            Regions = regions ?? throw new ArgumentNullException(nameof(regions));
            Coverage = coverage;
            Envelope = envelope;
        }

        public MixedLanguageSegment Segment { get; }
        public IReadOnlyList<OcrTextRegionSpan> Regions { get; }
        public SpatialAssistanceCoverage Coverage { get; }
        public ViewportEnvelope? Envelope { get; }
    }

    public sealed class SpatialAssistancePlan
    {
        public SpatialAssistancePlan(IReadOnlyList<SpatialAssistanceTarget> targets)
        {
            Targets = targets ?? throw new ArgumentNullException(nameof(targets));
        }

        public IReadOnlyList<SpatialAssistanceTarget> Targets { get; }
        public int ExactCount => Targets.Count(target => target.Coverage == SpatialAssistanceCoverage.Exact);
        public int PartialCount => Targets.Count(target => target.Coverage == SpatialAssistanceCoverage.Partial);
        public int UnresolvedCount => Targets.Count(target => target.Coverage == SpatialAssistanceCoverage.Unresolved);
    }

    public sealed class SemanticRegionAligner
    {
        private static readonly Regex TokenRegex = new Regex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*", RegexOptions.Compiled);

        public SpatialAssistancePlan Align(MixedLanguagePlan languagePlan, OcrTextAlignmentResult textLayout)
        {
            if (languagePlan == null) throw new ArgumentNullException(nameof(languagePlan));
            if (textLayout == null) throw new ArgumentNullException(nameof(textLayout));
            if (!string.Equals(languagePlan.SourceText, textLayout.SourceText, StringComparison.Ordinal))
                throw new ArgumentException("Language and OCR layouts must refer to the same source text.", nameof(textLayout));

            var targets = new List<SpatialAssistanceTarget>();
            foreach (var segment in languagePlan.Segments.Where(item => item.IsAssisted && item.Unit != null))
            {
                var unit = segment.Unit!;
                var overlapping = textLayout.ResolvedRegions
                    .Where(region => region.Overlaps(unit.Start, unit.End))
                    .OrderBy(region => region.SourceStart)
                    .ToArray();

                var coverage = DetermineCoverage(languagePlan.SourceText, unit.Start, unit.End, overlapping);
                var envelope = overlapping.Length > 0 ? ViewportEnvelope.FromRegions(overlapping) : (ViewportEnvelope?)null;
                targets.Add(new SpatialAssistanceTarget(segment, overlapping, coverage, envelope));
            }

            return new SpatialAssistancePlan(targets);
        }

        private static SpatialAssistanceCoverage DetermineCoverage(
            string sourceText,
            int start,
            int end,
            IReadOnlyList<OcrTextRegionSpan> regions)
        {
            if (regions.Count == 0) return SpatialAssistanceCoverage.Unresolved;

            var unitTokens = new List<CharSpan>();
            foreach (Match match in TokenRegex.Matches(sourceText.Substring(start, end - start)))
                unitTokens.Add(new CharSpan(start + match.Index, match.Length));

            if (unitTokens.Count == 0) return SpatialAssistanceCoverage.Unresolved;
            var covered = unitTokens.Count(token => regions.Any(region => region.SourceStart <= token.Start && region.SourceEnd >= token.End));
            if (covered == 0) return SpatialAssistanceCoverage.Unresolved;
            return covered == unitTokens.Count ? SpatialAssistanceCoverage.Exact : SpatialAssistanceCoverage.Partial;
        }

        private readonly struct CharSpan
        {
            public CharSpan(int start, int length) { Start = start; Length = length; }
            public int Start { get; }
            public int Length { get; }
            public int End => Start + Length;
        }
    }
}
