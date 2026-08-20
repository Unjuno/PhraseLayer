using System.Collections.Generic;
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
    public sealed class ReadObservationPipelineTests
    {
        [Fact]
        public async Task ExistingOcrObservationFlowsToExactSpatialAssistanceWithoutSecondOcrPass()
        {
            const string source = "Please keep off the grass.";
            var observation = new OcrObservation(
                source,
                0.99,
                new[]
                {
                    new OcrRegion("Please", 0.99, ImageQuad.FromRect(50, 100, 100, 50)),
                    new OcrRegion("keep", 0.99, ImageQuad.FromRect(170, 100, 100, 50)),
                    new OcrRegion("off", 0.99, ImageQuad.FromRect(290, 100, 100, 50)),
                    new OcrRegion("the", 0.99, ImageQuad.FromRect(410, 100, 100, 50)),
                    new OcrRegion("grass", 0.99, ImageQuad.FromRect(530, 100, 100, 50)),
                });
            var frame = new ImageFrame(new byte[4], 1000, 500, 123456);
            var learner = new InMemoryLearnerModel(0.99);
            learner.SetUnderstanding("keep off", 0.05);
            var language = new LanguagePipeline(
                new RuleBasedSemanticSegmenter(new[] { "keep off" }),
                learner,
                new AssistancePlanner(),
                new DictionaryTranslationEngine(new Dictionary<string, string>
                {
                    ["keep off"] = "立ち入らない"
                }));
            var pipeline = new ReadObservationPipeline(language);

            var result = await pipeline.ProcessAsync(
                frame,
                observation,
                AssistancePolicy.ForMode(AssistanceMode.Challenge));

            Assert.Same(frame, result.Frame);
            Assert.Same(observation, result.Observation);
            Assert.Equal("Please 立ち入らない the grass.", result.LanguagePlan.DisplayText);
            Assert.Equal(5, result.ViewportRegions.Count);
            Assert.Empty(result.TextAlignment.UnresolvedRegions);

            var target = Assert.Single(result.SpatialAssistance.Targets);
            Assert.Equal(SpatialAssistanceCoverage.Exact, target.Coverage);
            Assert.Equal("keep off", target.Segment.SourceText);
            Assert.Equal("立ち入らない", target.Segment.DisplayText);
            Assert.Equal(2, target.Regions.Count);
            Assert.NotNull(target.Envelope);
        }

        [Fact]
        public async Task ReadModePipelineUsesSameDownstreamSpatialContractAfterOcr()
        {
            const string source = "Please keep off the grass.";
            var observation = new OcrObservation(
                source,
                0.99,
                new[]
                {
                    new OcrRegion("keep off", 0.99, ImageQuad.FromRect(100, 100, 240, 60))
                });
            var frame = new ImageFrame(new byte[4], 800, 400, 777);
            var learner = new InMemoryLearnerModel(0.99);
            learner.SetUnderstanding("keep off", 0.05);
            var language = new LanguagePipeline(
                new RuleBasedSemanticSegmenter(new[] { "keep off" }),
                learner,
                new AssistancePlanner(),
                new DictionaryTranslationEngine(new Dictionary<string, string>
                {
                    ["keep off"] = "立ち入らない"
                }));
            var pipeline = new ReadModePipeline(new FakeOcrEngine(observation), language);

            var result = await pipeline.ProcessSpatialAsync(
                frame,
                AssistancePolicy.ForMode(AssistanceMode.Challenge));

            Assert.Same(observation, result.Observation);
            Assert.Single(result.SpatialAssistance.Targets);
            Assert.Equal(SpatialAssistanceCoverage.Exact, result.SpatialAssistance.Targets[0].Coverage);
        }
    }
}
