using System.Collections.Generic;
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
    public sealed class SpatialTextAlignmentTests
    {
        [Fact]
        public void OcrRegionsAlignToSourceTokenSpansInReadingOrder()
        {
            const string source = "Please keep off the grass.";
            var observation = BuildObservation(source, "Please", "KEEP", "off", "grass");
            var frame = new ImageFrame(new byte[4], 1000, 500, 0);
            var viewport = OcrViewportMapper.Map(observation, frame);

            var result = new OcrRegionTextAligner().Align(observation, viewport);

            Assert.Equal(4, result.ResolvedRegions.Count);
            Assert.Equal("Please", source.Substring(result.ResolvedRegions[0].SourceStart, result.ResolvedRegions[0].SourceLength));
            Assert.Equal("keep", source.Substring(result.ResolvedRegions[1].SourceStart, result.ResolvedRegions[1].SourceLength));
            Assert.Equal("off", source.Substring(result.ResolvedRegions[2].SourceStart, result.ResolvedRegions[2].SourceLength));
            Assert.Equal("grass", source.Substring(result.ResolvedRegions[3].SourceStart, result.ResolvedRegions[3].SourceLength));
        }

        [Fact]
        public void RepeatedWordsReceiveDifferentSourceOccurrences()
        {
            const string source = "go go home";
            var observation = BuildObservation(source, "go", "go", "home");
            var frame = new ImageFrame(new byte[4], 1000, 500, 0);
            var result = new OcrRegionTextAligner().Align(observation, OcrViewportMapper.Map(observation, frame));

            Assert.Equal(0, result.ResolvedRegions[0].SourceStart);
            Assert.Equal(3, result.ResolvedRegions[1].SourceStart);
            Assert.Equal(6, result.ResolvedRegions[2].SourceStart);
        }

        [Fact]
        public async System.Threading.Tasks.Task AssistedMultiwordExpressionMapsAcrossTwoWordBoxes()
        {
            const string source = "Please keep off the grass.";
            var observation = BuildObservation(source, "Please", "keep", "off", "the", "grass");
            var frame = new ImageFrame(new byte[4], 1000, 500, 0);
            var languagePlan = await BuildKeepOffPlan(source);
            var textLayout = new OcrRegionTextAligner().Align(observation, OcrViewportMapper.Map(observation, frame));

            var spatial = new SemanticRegionAligner().Align(languagePlan, textLayout);

            var target = Assert.Single(spatial.Targets);
            Assert.Equal("keep off", target.Segment.SourceText);
            Assert.Equal("立ち入らない", target.Segment.DisplayText);
            Assert.Equal(SpatialAssistanceCoverage.Exact, target.Coverage);
            Assert.Equal(2, target.Regions.Count);
            Assert.NotNull(target.Envelope);
            Assert.Equal(1, spatial.ExactCount);
        }

        [Fact]
        public async System.Threading.Tasks.Task MissingWordBoxProducesPartialCoverageInsteadOfFalseExactPlacement()
        {
            const string source = "Please keep off the grass.";
            var observation = BuildObservation(source, "Please", "keep", "the", "grass");
            var frame = new ImageFrame(new byte[4], 1000, 500, 0);
            var languagePlan = await BuildKeepOffPlan(source);
            var textLayout = new OcrRegionTextAligner().Align(observation, OcrViewportMapper.Map(observation, frame));

            var target = Assert.Single(new SemanticRegionAligner().Align(languagePlan, textLayout).Targets);

            Assert.Equal(SpatialAssistanceCoverage.Partial, target.Coverage);
            Assert.Single(target.Regions);
        }

        [Fact]
        public async System.Threading.Tasks.Task UnknownOcrTextDoesNotGetAttachedToSemanticUnit()
        {
            const string source = "Please keep off the grass.";
            var observation = BuildObservation(source, "Please", "completely different", "grass");
            var frame = new ImageFrame(new byte[4], 1000, 500, 0);
            var languagePlan = await BuildKeepOffPlan(source);
            var textLayout = new OcrRegionTextAligner().Align(observation, OcrViewportMapper.Map(observation, frame));

            Assert.Single(textLayout.UnresolvedRegions);
            var target = Assert.Single(new SemanticRegionAligner().Align(languagePlan, textLayout).Targets);
            Assert.Equal(SpatialAssistanceCoverage.Unresolved, target.Coverage);
            Assert.Empty(target.Regions);
            Assert.Null(target.Envelope);
        }

        private static OcrObservation BuildObservation(string source, params string[] regionTexts)
        {
            var regions = new List<OcrRegion>();
            for (var index = 0; index < regionTexts.Length; index++)
            {
                regions.Add(new OcrRegion(
                    regionTexts[index],
                    0.99,
                    ImageQuad.FromRect(50 + (index * 120), 100, 100, 50)));
            }
            return new OcrObservation(source, 0.99, regions);
        }

        private static System.Threading.Tasks.Task<MixedLanguagePlan> BuildKeepOffPlan(string source)
        {
            var learner = new InMemoryLearnerModel(0.99);
            learner.SetUnderstanding("keep off", 0.05);
            var pipeline = new LanguagePipeline(
                new RuleBasedSemanticSegmenter(new[] { "keep off" }),
                learner,
                new AssistancePlanner(),
                new DictionaryTranslationEngine(new Dictionary<string, string> { ["keep off"] = "立ち入らない" }));

            return pipeline.PlanAsync(source, AssistancePolicy.ForMode(AssistanceMode.Challenge), source);
        }
    }
}
