using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class LearnerAttributionTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" \t ")]
        [InlineData("alpha")]
        [InlineData(" ALPHA ")]
        public async Task MissingOrUnchangedTranslationCannotCreateAutomaticExposureOrSuccess(string? translation)
        {
            foreach (var completion in new[] { false, true })
            {
                var model = new InMemoryLearnerModel(0.55);
                var plan = await Pipeline(model, translation).PlanAsync("alpha.", AssistancePolicy.ForMode(AssistanceMode.Auto));
                Assert.Single(plan.Assistance.Decisions);
                var result = new LearningEncounterSession(plan, new LearnerAdaptationEngine(model)).Finish(completion);
                Assert.Empty(result.Updates);
                Assert.Empty(model.CreateSnapshot().Entries);
            }
        }
        [Fact]
        public async Task AvailableTranslatedTextReceivesExposureButNeverPassiveMastery()
        {
            var model = new InMemoryLearnerModel(0.55);
            for (var encounter = 0; encounter < 100; encounter++)
            {
                var plan = await Pipeline(model, "試験訳").PlanAsync("alpha.", AssistancePolicy.ForMode(AssistanceMode.Auto));
                var result = new LearningEncounterSession(plan, new LearnerAdaptationEngine(model)).Finish();
                Assert.Equal(LearningEvidenceKind.AssistedExposure, Assert.Single(result.Updates).Evidence);
            }
            var unit = new RuleBasedSemanticSegmenter().Segment("alpha.").OfKind(SemanticUnitKind.Word).Single();
            Assert.InRange(model.Estimate(unit).Understanding, 0.55, 0.80);
            Assert.NotEqual(KnowledgeState.Known, model.Estimate(unit).State);
        }
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        [InlineData(16)]
        [InlineData(32)]
        public void OneCompletionRewardsOneNormalizedKeyRegardlessOfRepeatedOccurrences(int occurrences)
        {
            var model = new InMemoryLearnerModel(0.5);
            var source = string.Join(" ", Enumerable.Range(0, occurrences).Select(index => index % 2 == 0 ? "go" : "GO")) + ".";
            var plan = Unassisted(source);
            var result = new LearningEncounterSession(plan, new LearnerAdaptationEngine(model)).Finish(true);
            Assert.Single(result.Updates);
            Assert.Equal(0.54, model.Estimate(plan.Document!.OfKind(SemanticUnitKind.Word).First()).Understanding, 12);
        }
        [Fact]
        public void ExplicitNegativeEvidenceOnOneOccurrenceSuppressesAutomaticPositiveEvidenceOnDuplicates()
        {
            var model = new InMemoryLearnerModel(0.5);
            var plan = Unassisted("go GO go.");
            var words = plan.Document!.OfKind(SemanticUnitKind.Word).ToArray();
            var session = new LearningEncounterSession(plan, new LearnerAdaptationEngine(model));
            session.Record(words[1], LearningEvidenceKind.RecallFailed);
            Assert.Equal(LearningEvidenceKind.RecallFailed, Assert.Single(session.Finish(true).Updates).Evidence);
            Assert.Equal(0.35, model.Estimate(words[0]).Understanding, 12);
        }
        [Fact]
        public async Task ExplicitRecallRemainsAvailableWhenTranslationIsMissing()
        {
            var model = new InMemoryLearnerModel(0.5);
            var plan = await Pipeline(model, null).PlanAsync("alpha.", AssistancePolicy.ForMode(AssistanceMode.Auto));
            var unit = plan.Document!.OfKind(SemanticUnitKind.Word).Single();
            var session = new LearningEncounterSession(plan, new LearnerAdaptationEngine(model));
            session.Record(unit, LearningEvidenceKind.RecallSucceeded);
            Assert.Equal(LearningEvidenceKind.RecallSucceeded, Assert.Single(session.Finish().Updates).Evidence);
            Assert.Equal(0.6, model.Estimate(unit).Understanding, 12);
        }
        [Fact]
        public void DistinctKnowledgeKeysStillReceiveIndependentAutomaticUpdates()
        {
            var model = new InMemoryLearnerModel(0.5);
            var result = new LearningEncounterSession(Unassisted("alpha beta alpha."), new LearnerAdaptationEngine(model)).Finish(true);
            Assert.Equal(2, result.Updates.Count);
            Assert.All(result.Updates, item => Assert.Equal(0.54, item.UpdatedUnderstanding, 12));
        }
        private static MixedLanguagePlan Unassisted(string source) => new MixedLanguagePlan(source,
            new[] { new MixedLanguageSegment(source, source, false, null) }, new AssistancePlan(Array.Empty<AssistanceDecision>(), 0, 0),
            new RuleBasedSemanticSegmenter().Segment(source));
        private static LanguagePipeline Pipeline(InMemoryLearnerModel model, string? output) => new LanguagePipeline(
            new RuleBasedSemanticSegmenter(), model, new AssistancePlanner(), new FixedTranslator(output));
        private sealed class FixedTranslator : ITranslationEngine
        {
            private readonly string? output;
            public FixedTranslator(string? output) { this.output = output; }
            public Task<string> TranslateAsync(string sourceText, string context, CancellationToken cancellationToken = default)
            { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(output!); }
        }
    }
}
