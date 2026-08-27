using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Translation;

namespace PhraseLayer.Core.Pipeline
{
    public sealed class MixedLanguageSegment
    {
        public MixedLanguageSegment(string sourceText, string displayText, bool isAssisted, SemanticUnit? unit)
        { SourceText = sourceText; DisplayText = displayText; IsAssisted = isAssisted; Unit = unit; }
        public string SourceText { get; }
        public string DisplayText { get; }
        public bool IsAssisted { get; }
        public SemanticUnit? Unit { get; }
    }

    public sealed class MixedLanguagePlan
    {
        public MixedLanguagePlan(string sourceText, IReadOnlyList<MixedLanguageSegment> segments, AssistancePlan assistance)
            : this(sourceText, segments, assistance, null)
        {
        }

        public MixedLanguagePlan(
            string sourceText,
            IReadOnlyList<MixedLanguageSegment> segments,
            AssistancePlan assistance,
            SemanticDocument? document)
        {
            SourceText = sourceText ?? throw new ArgumentNullException(nameof(sourceText));
            Segments = segments ?? throw new ArgumentNullException(nameof(segments));
            Assistance = assistance ?? throw new ArgumentNullException(nameof(assistance));
            if (document != null && !string.Equals(document.SourceText, sourceText, StringComparison.Ordinal))
                throw new ArgumentException("Semantic document source text must match the plan source text.", nameof(document));
            Document = document;
        }

        public string SourceText { get; }
        public IReadOnlyList<MixedLanguageSegment> Segments { get; }
        public AssistancePlan Assistance { get; }
        public SemanticDocument? Document { get; }
        public string DisplayText => string.Concat(Segments.Select(segment => segment.DisplayText));
    }

    /// <summary>
    /// Builds mixed-language plans while preserving the caller synchronization context across translation awaits.
    /// Platform translation adapters may be bound to a Unity/render thread just like OCR adapters.
    /// </summary>
    public sealed class LanguagePipeline
    {
        private readonly ISemanticSegmenter _segmenter;
        private readonly ILearnerModel _learner;
        private readonly AssistancePlanner _planner;
        private readonly ITranslationEngine _translator;
        public LanguagePipeline(ISemanticSegmenter segmenter, ILearnerModel learner, AssistancePlanner planner, ITranslationEngine translator)
        { _segmenter = segmenter; _learner = learner; _planner = planner; _translator = translator; }

        public async Task<MixedLanguagePlan> PlanAsync(string sourceText, AssistancePolicy policy, string? context = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var document = _segmenter.Segment(sourceText);
            var assistance = _planner.Plan(document, _learner, policy);
            var segments = new List<MixedLanguageSegment>();
            var cursor = 0;
            foreach (var decision in assistance.Decisions.OrderBy(item => item.Unit.Start))
            {
                var unit = decision.Unit;
                if (unit.Start < cursor) throw new InvalidOperationException("Assistance decisions overlap.");
                if (unit.Start > cursor)
                {
                    var untouched = sourceText.Substring(cursor, unit.Start - cursor);
                    segments.Add(new MixedLanguageSegment(untouched, untouched, false, null));
                }
                var translated = await _translator.TranslateAsync(unit.Text, context ?? sourceText, cancellationToken);
                if (string.IsNullOrWhiteSpace(translated)) translated = unit.Text;
                segments.Add(new MixedLanguageSegment(unit.Text, translated, true, unit));
                cursor = unit.End;
            }
            if (cursor < sourceText.Length)
            {
                var rest = sourceText.Substring(cursor);
                segments.Add(new MixedLanguageSegment(rest, rest, false, null));
            }
            if (segments.Count == 0) segments.Add(new MixedLanguageSegment(sourceText, sourceText, false, null));
            return new MixedLanguagePlan(sourceText, segments, assistance, document);
        }
    }

    public sealed class AssistanceSessionCache
    {
        private readonly Dictionary<string, MixedLanguagePlan> _plans = new Dictionary<string, MixedLanguagePlan>(StringComparer.Ordinal);
        public bool TryGet(string encounterId, out MixedLanguagePlan? plan) => _plans.TryGetValue(encounterId, out plan);
        public MixedLanguagePlan Store(string encounterId, MixedLanguagePlan plan) { _plans[encounterId] = plan; return plan; }
        public void EndEncounter(string encounterId) => _plans.Remove(encounterId);
        public void Clear() => _plans.Clear();
    }

    public sealed class ReadModeSpatialResult
    {
        public ReadModeSpatialResult(
            ImageFrame frame,
            OcrObservation observation,
            IReadOnlyList<OcrViewportRegion> viewportRegions,
            MixedLanguagePlan languagePlan)
        {
            Frame = frame ?? throw new ArgumentNullException(nameof(frame));
            Observation = observation ?? throw new ArgumentNullException(nameof(observation));
            ViewportRegions = viewportRegions ?? throw new ArgumentNullException(nameof(viewportRegions));
            LanguagePlan = languagePlan ?? throw new ArgumentNullException(nameof(languagePlan));
        }

        public ImageFrame Frame { get; }
        public OcrObservation Observation { get; }
        public IReadOnlyList<OcrViewportRegion> ViewportRegions { get; }
        public MixedLanguagePlan LanguagePlan { get; }
    }

    /// <summary>
    /// Owns OCR only. Once a frame has been recognized, the exact observation is delegated to
    /// ReadModeObservationProcessor so language planning and geometry alignment never need a second OCR inference.
    /// </summary>
    public sealed class ReadModePipeline
    {
        private readonly IOcrEngine _ocr;
        private readonly ReadModeObservationProcessor _observations;

        public ReadModePipeline(IOcrEngine ocr, LanguagePipeline language)
        {
            _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
            _observations = new ReadModeObservationProcessor(
                language ?? throw new ArgumentNullException(nameof(language)));
        }

        public async Task<MixedLanguagePlan> ProcessAsync(
            ImageFrame frame,
            AssistancePolicy policy,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = await ProcessSpatialAsync(frame, policy, cancellationToken);
            return result.LanguagePlan;
        }

        public async Task<ReadModeSpatialResult> ProcessSpatialAsync(
            ImageFrame frame,
            AssistancePolicy policy,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (policy == null) throw new ArgumentNullException(nameof(policy));

            var observation = await _ocr.RecognizeAsync(frame, cancellationToken);
            return await _observations.ProcessSpatialAsync(frame, observation, policy, cancellationToken);
        }

        public async Task<ReadModeAlignedResult> ProcessAlignedAsync(
            ImageFrame frame,
            AssistancePolicy policy,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (policy == null) throw new ArgumentNullException(nameof(policy));

            var observation = await _ocr.RecognizeAsync(frame, cancellationToken);
            return await _observations.ProcessAlignedAsync(frame, observation, policy, cancellationToken);
        }
    }

    public sealed class ListenModePipeline
    {
        private readonly IAsrEngine _asr; private readonly LanguagePipeline _language;
        public ListenModePipeline(IAsrEngine asr, LanguagePipeline language) { _asr = asr; _language = language; }
        public async Task<MixedLanguagePlan> ProcessAsync(AudioChunk audio, AssistancePolicy policy, CancellationToken cancellationToken = default(CancellationToken))
        {
            var observation = await _asr.TranscribeAsync(audio, cancellationToken);
            return await _language.PlanAsync(observation.Text, policy, observation.Text, cancellationToken);
        }
    }
}
