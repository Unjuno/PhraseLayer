using System;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Spatial;

namespace PhraseLayer.Core.Pipeline
{
    /// <summary>
    /// Result of applying the adaptive language plan to an already-recognized OCR observation and aligning
    /// every assisted semantic segment back to the OCR geometry from that exact frame.
    /// </summary>
    public sealed class ReadModeAlignedResult
    {
        public ReadModeAlignedResult(
            ReadModeSpatialResult spatial,
            OcrTextAlignmentResult textAlignment,
            SpatialAssistancePlan spatialAssistance)
        {
            Spatial = spatial ?? throw new ArgumentNullException(nameof(spatial));
            TextAlignment = textAlignment ?? throw new ArgumentNullException(nameof(textAlignment));
            SpatialAssistance = spatialAssistance ?? throw new ArgumentNullException(nameof(spatialAssistance));
        }

        public ReadModeSpatialResult Spatial { get; }
        public OcrTextAlignmentResult TextAlignment { get; }
        public SpatialAssistancePlan SpatialAssistance { get; }
    }

    /// <summary>
    /// Consumes an OCR observation that has already been produced for a frame. This stage deliberately does not
    /// own an IOcrEngine, so a live camera pump can recognize a frame exactly once and reuse the same observation
    /// for adaptive language planning, viewport mapping, and semantic-to-physical text alignment.
    /// </summary>
    public sealed class ReadModeObservationProcessor
    {
        private readonly LanguagePipeline language;
        private readonly OcrRegionTextAligner textAligner;
        private readonly SemanticRegionAligner semanticAligner;

        public ReadModeObservationProcessor(
            LanguagePipeline language,
            OcrRegionTextAligner? textAligner = null,
            SemanticRegionAligner? semanticAligner = null)
        {
            this.language = language ?? throw new ArgumentNullException(nameof(language));
            this.textAligner = textAligner ?? new OcrRegionTextAligner();
            this.semanticAligner = semanticAligner ?? new SemanticRegionAligner();
        }

        public async Task<ReadModeSpatialResult> ProcessSpatialAsync(
            ImageFrame frame,
            OcrObservation observation,
            AssistancePolicy policy,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            if (policy == null) throw new ArgumentNullException(nameof(policy));

            cancellationToken.ThrowIfCancellationRequested();
            var languagePlan = await language.PlanAsync(
                observation.Text,
                policy,
                observation.Text,
                cancellationToken);
            var viewportRegions = OcrViewportMapper.Map(observation, frame);
            return new ReadModeSpatialResult(frame, observation, viewportRegions, languagePlan);
        }

        public async Task<ReadModeAlignedResult> ProcessAlignedAsync(
            ImageFrame frame,
            OcrObservation observation,
            AssistancePolicy policy,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var spatial = await ProcessSpatialAsync(frame, observation, policy, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var textAlignment = textAligner.Align(spatial.Observation, spatial.ViewportRegions);
            var spatialAssistance = semanticAligner.Align(spatial.LanguagePlan, textAlignment);
            return new ReadModeAlignedResult(spatial, textAlignment, spatialAssistance);
        }
    }
}
