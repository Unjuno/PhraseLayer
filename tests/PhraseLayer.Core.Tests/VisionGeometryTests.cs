using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class VisionGeometryTests
    {
        [Fact]
        public void ImagePointConversionFlipsTopLeftYIntoBottomLeftViewportY()
        {
            var point = ImageCoordinateMapper.ToViewport(new ImagePoint(200, 50), 1000, 500);
            Assert.Equal(0.20, point.U, 6);
            Assert.Equal(0.90, point.V, 6);
        }

        [Fact]
        public void DetectorOvershootIsClampedToViewportBounds()
        {
            var topLeft = ImageCoordinateMapper.ToViewport(new ImagePoint(-20, -10), 1000, 500);
            var bottomRight = ImageCoordinateMapper.ToViewport(new ImagePoint(1100, 700), 1000, 500);
            Assert.Equal(0.0, topLeft.U, 6);
            Assert.Equal(1.0, topLeft.V, 6);
            Assert.Equal(1.0, bottomRight.U, 6);
            Assert.Equal(0.0, bottomRight.V, 6);
        }

        [Fact]
        public void RectQuadMapsToExpectedViewportCenter()
        {
            var imageQuad = ImageQuad.FromRect(100, 50, 200, 100);
            var viewport = ImageCoordinateMapper.ToViewport(imageQuad, 1000, 500);
            Assert.Equal(0.20, viewport.Centroid.U, 6);
            Assert.Equal(0.80, viewport.Centroid.V, 6);
        }

        [Fact]
        public void OcrRegionsPreserveRotatedQuadAndExposeViewportAnchor()
        {
            var region = new OcrRegion(
                "keep off",
                0.97,
                new ImageQuad(
                    new ImagePoint(100, 100),
                    new ImagePoint(300, 80),
                    new ImagePoint(310, 180),
                    new ImagePoint(110, 200)));
            var observation = new OcrObservation("keep off", 0.97, new[] { region });
            var frame = new ImageFrame(new byte[4], 1000, 500, 1234);

            var mapped = Assert.Single(OcrViewportMapper.Map(observation, frame));
            Assert.Same(region, mapped.Source);
            Assert.Equal(0.205, mapped.Anchor.U, 6);
            Assert.Equal(0.72, mapped.Anchor.V, 6);
        }

        [Fact]
        public void OcrObservationSnapshotsRegionCollection()
        {
            var regions = new List<OcrRegion>
            {
                new OcrRegion("keep off", 0.9, ImageQuad.FromRect(0, 0, 10, 10))
            };

            var observation = new OcrObservation("keep off", 0.9, regions);
            regions.Clear();

            Assert.Single(observation.Regions);
        }

        [Fact]
        public async Task SpatialReadModeReturnsLanguagePlanAndViewportRegionsFromOneOcrPass()
        {
            var ocrRegion = new OcrRegion("keep off", 0.99, ImageQuad.FromRect(100, 50, 200, 100));
            var observation = new OcrObservation("Please keep off the grass.", 0.99, new[] { ocrRegion });
            var countingOcr = new CountingOcrEngine(observation);

            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding("keep off", 0.10);
            var language = new LanguagePipeline(
                new RuleBasedSemanticSegmenter(new[] { "keep off" }),
                learner,
                new AssistancePlanner(),
                new DictionaryTranslationEngine(new Dictionary<string, string> { ["keep off"] = "立ち入らない" }));
            var read = new ReadModePipeline(countingOcr, language);
            var frame = new ImageFrame(new byte[4], 1000, 500, 42);

            var result = await read.ProcessSpatialAsync(frame, AssistancePolicy.ForMode(AssistanceMode.Balanced));

            Assert.Equal(1, countingOcr.CallCount);
            Assert.Same(frame, result.Frame);
            Assert.Same(observation, result.Observation);
            Assert.Equal("Please 立ち入らない the grass.", result.LanguagePlan.DisplayText);
            var viewport = Assert.Single(result.ViewportRegions);
            Assert.Equal(0.20, viewport.Anchor.U, 6);
            Assert.Equal(0.80, viewport.Anchor.V, 6);
        }

        [Fact]
        public void LegacyOcrObservationRemainsSpatiallyEmpty()
        {
            var observation = new OcrObservation("plain text", 1.0);
            Assert.Empty(observation.Regions);
        }

        private sealed class CountingOcrEngine : IOcrEngine
        {
            private readonly OcrObservation observation;

            public CountingOcrEngine(OcrObservation observation)
            {
                this.observation = observation;
            }

            public int CallCount { get; private set; }

            public Task<OcrObservation> RecognizeAsync(
                ImageFrame frame,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                return Task.FromResult(observation);
            }
        }
    }
}
