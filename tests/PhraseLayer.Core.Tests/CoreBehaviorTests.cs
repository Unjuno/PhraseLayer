using System.Collections.Generic;
using System.Linq;
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
    public sealed class CoreBehaviorTests
    {
        [Fact]
        public void SegmenterUsesLongestMweAndCleanClauseSpans()
        {
            var segmenter = new RuleBasedSemanticSegmenter(new[] { "keep", "keep off" });
            var mweDoc = segmenter.Segment("Please keep off the grass.");
            Assert.Equal("keep off", Assert.Single(mweDoc.OfKind(SemanticUnitKind.MultiwordExpression)).Text, ignoreCase: true);

            var clauseDoc = segmenter.Segment("I was tired, so I went home, and I fell asleep immediately.");
            Assert.Equal(new[] { "I was tired", "so I went home", "and I fell asleep immediately" },
                clauseDoc.OfKind(SemanticUnitKind.Clause).Select(unit => unit.Text).ToArray());
        }

        [Fact]
        public void PlannerCanSelectOnlyDifficultMiddleClause()
        {
            var document = new RuleBasedSemanticSegmenter().Segment("I was tired, so I went home, and I fell asleep immediately.");
            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding("I was tired", 0.95);
            learner.SetUnderstanding("so I went home", 0.15);
            learner.SetUnderstanding("and I fell asleep immediately", 0.95);
            var plan = new AssistancePlanner().Plan(document, learner, AssistancePolicy.ForMode(AssistanceMode.Balanced));
            var decision = Assert.Single(plan.Decisions);
            Assert.Equal(SemanticUnitKind.Clause, decision.Unit.Kind);
            Assert.Equal("so I went home", decision.Unit.Text);
        }

        [Fact]
        public void AutoModeRaisesTargetSupportForLowerUnderstanding()
        {
            var document = new RuleBasedSemanticSegmenter().Segment("One two three four.");
            var planner = new AssistancePlanner();
            var high = planner.Plan(document, new InMemoryLearnerModel(0.9), AssistancePolicy.ForMode(AssistanceMode.Auto));
            var low = planner.Plan(document, new InMemoryLearnerModel(0.2), AssistancePolicy.ForMode(AssistanceMode.Auto));
            Assert.True(low.TargetRatio > high.TargetRatio);
        }

        [Fact]
        public async Task PipelineRendersInPlaceWithoutMarkersAndDropsHelpWhenKnown()
        {
            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding("keep off", 0.10);
            var pipeline = BuildPipeline(learner);
            var first = await pipeline.PlanAsync("Please keep off the grass.", AssistancePolicy.ForMode(AssistanceMode.Balanced));
            Assert.Equal("Please 立ち入らない the grass.", first.DisplayText);
            Assert.DoesNotContain("[", first.DisplayText);
            Assert.DoesNotContain("]", first.DisplayText);

            learner.SetUnderstanding("keep off", 0.98);
            var second = await pipeline.PlanAsync("Please keep off the grass.", AssistancePolicy.ForMode(AssistanceMode.Balanced));
            Assert.Equal("Please keep off the grass.", second.DisplayText);
        }

        [Fact]
        public async Task EncounterCacheFreezesVisiblePlan()
        {
            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding("keep off", 0.10);
            var pipeline = BuildPipeline(learner);
            var cache = new AssistanceSessionCache();
            cache.Store("view-1", await pipeline.PlanAsync("Please keep off the grass.", AssistancePolicy.ForMode(AssistanceMode.Balanced)));
            learner.SetUnderstanding("keep off", 0.99);
            var recomputed = await pipeline.PlanAsync("Please keep off the grass.", AssistancePolicy.ForMode(AssistanceMode.Balanced));
            Assert.True(cache.TryGet("view-1", out var frozen));
            Assert.Equal("Please 立ち入らない the grass.", frozen!.DisplayText);
            Assert.Equal("Please keep off the grass.", recomputed.DisplayText);
        }

        [Fact]
        public async Task FakeReadAndListenModesShareLanguagePipeline()
        {
            const string source = "Please keep off the grass.";
            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding("keep off", 0.10);
            var language = BuildPipeline(learner);
            var read = new ReadModePipeline(new FakeOcrEngine(source), language);
            var listen = new ListenModePipeline(new FakeAsrEngine(source), language);
            var policy = AssistancePolicy.ForMode(AssistanceMode.Balanced);
            var readPlan = await read.ProcessAsync(new ImageFrame(new byte[4], 1, 1, 0), policy);
            var listenPlan = await listen.ProcessAsync(new AudioChunk(new float[160], 16000, 0), policy);
            Assert.Equal(readPlan.DisplayText, listenPlan.DisplayText);
            Assert.Equal("Please 立ち入らない the grass.", readPlan.DisplayText);
        }

        private static LanguagePipeline BuildPipeline(InMemoryLearnerModel learner)
        {
            return new LanguagePipeline(
                new RuleBasedSemanticSegmenter(new[] { "keep off" }), learner, new AssistancePlanner(),
                new DictionaryTranslationEngine(new Dictionary<string, string> { ["keep off"] = "立ち入らない" }));
        }
    }
}
