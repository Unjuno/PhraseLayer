using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Spatial;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class ReadModeObservationProcessorTests
    {
        private const string Source = "Please keep off the grass.";

        [Fact]
        public async Task AlreadyRecognizedObservationProducesExactSemanticSpatialTarget()
        {
            var processor = new ReadModeObservationProcessor(BuildLanguagePipeline());
            var frame = new ImageFrame(new byte[4], 1000, 500, 42);
            var observation = BuildObservation();

            var result = await processor.ProcessAlignedAsync(
                frame,
                observation,
                AssistancePolicy.ForMode(AssistanceMode.Challenge));

            Assert.Same(frame, result.Spatial.Frame);
            Assert.Same(observation, result.Spatial.Observation);
            Assert.Equal("Please 立ち入らない the grass.", result.Spatial.LanguagePlan.DisplayText);
            Assert.Equal(5, result.Spatial.ViewportRegions.Count);
            Assert.Equal(5, result.TextAlignment.ResolvedRegions.Count);

            var target = Assert.Single(result.SpatialAssistance.Targets);
            Assert.Equal("keep off", target.Segment.SourceText);
            Assert.Equal("立ち入らない", target.Segment.DisplayText);
            Assert.Equal(SpatialAssistanceCoverage.Exact, target.Coverage);
            Assert.Equal(2, target.Regions.Count);
            Assert.NotNull(target.Envelope);
        }

        [Fact]
        public async Task ReadModeAlignedPipelineRunsOcrExactlyOnce()
        {
            var observation = BuildObservation();
            var ocr = new CountingOcrEngine(observation);
            var pipeline = new ReadModePipeline(ocr, BuildLanguagePipeline());
            var frame = new ImageFrame(new byte[4], 1000, 500, 43);

            var result = await pipeline.ProcessAlignedAsync(
                frame,
                AssistancePolicy.ForMode(AssistanceMode.Challenge));

            Assert.Equal(1, ocr.CallCount);
            Assert.Same(frame, ocr.LastFrame);
            Assert.Same(observation, result.Spatial.Observation);
            Assert.Equal("Please 立ち入らない the grass.", result.Spatial.LanguagePlan.DisplayText);
            Assert.Equal(SpatialAssistanceCoverage.Exact, Assert.Single(result.SpatialAssistance.Targets).Coverage);
        }

        [Fact]
        public async Task PartialOcrGeometryRemainsPartialAfterAdaptivePlanning()
        {
            var processor = new ReadModeObservationProcessor(BuildLanguagePipeline());
            var frame = new ImageFrame(new byte[4], 1000, 500, 44);
            var observation = BuildObservation(includeOffRegion: false);

            var result = await processor.ProcessAlignedAsync(
                frame,
                observation,
                AssistancePolicy.ForMode(AssistanceMode.Challenge));

            var target = Assert.Single(result.SpatialAssistance.Targets);
            Assert.Equal(SpatialAssistanceCoverage.Partial, target.Coverage);
            Assert.Single(target.Regions);
        }

        private static LanguagePipeline BuildLanguagePipeline()
        {
            var learner = new InMemoryLearnerModel(0.99);
            learner.SetUnderstanding("keep off", 0.05);
            return new LanguagePipeline(
                new RuleBasedSemanticSegmenter(new[] { "keep off" }),
                learner,
                new AssistancePlanner(),
                new DictionaryTranslationEngine(new Dictionary<string, string>
                {
                    ["keep off"] = "立ち入らない"
                }));
        }

        private static OcrObservation BuildObservation(bool includeOffRegion = true)
        {
            var regions = new List<OcrRegion>
            {
                Region("Please", 50),
                Region("keep", 170)
            };
            if (includeOffRegion)
                regions.Add(Region("off", 290));
            regions.Add(Region("the", 410));
            regions.Add(Region("grass", 530));
            return new OcrObservation(Source, 0.99, regions);
        }

        private static OcrRegion Region(string text, double x)
        {
            return new OcrRegion(text, 0.99, ImageQuad.FromRect(x, 100, 100, 50));
        }

        private sealed class CountingOcrEngine : IOcrEngine
        {
            private readonly OcrObservation observation;

            public CountingOcrEngine(OcrObservation observation)
            {
                this.observation = observation;
            }

            public int CallCount { get; private set; }
            public ImageFrame? LastFrame { get; private set; }

            public Task<OcrObservation> RecognizeAsync(
                ImageFrame frame,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                LastFrame = frame;
                return Task.FromResult(observation);
            }
        }
    }
}
