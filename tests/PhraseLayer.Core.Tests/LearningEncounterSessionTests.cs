using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class LearningEncounterSessionTests
    {
        [Fact]
        public async Task LanguagePlanRetainsSemanticDocument()
        {
            var learner = CreateLearner();
            var plan = await BuildPipeline(learner).PlanAsync(Source, AssistancePolicy.ForMode(AssistanceMode.Balanced));

            Assert.NotNull(plan.Document);
            Assert.Equal(Source, plan.Document!.SourceText);
            Assert.Contains(plan.Document.OfKind(SemanticUnitKind.MultiwordExpression), unit =>
                string.Equals(unit.Text, "keep off", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task FinishWithoutExplicitEvidenceDoesNotMutateLearnerState()
        {
            var learner = CreateLearner();
            var plan = await BuildPipeline(learner).PlanAsync(Source, AssistancePolicy.ForMode(AssistanceMode.Balanced));
            var keepOff = GetUnit(plan, "keep off", SemanticUnitKind.MultiwordExpression);
            var please = GetUnit(plan, "Please", SemanticUnitKind.Word);
            var beforeKeepOff = learner.Estimate(keepOff).Understanding;
            var beforePlease = learner.Estimate(please).Understanding;
            var session = new LearningEncounterSession(plan, new LearnerAdaptationEngine(learner));

            var summary = session.Finish();

            Assert.Empty(summary.Updates);
            Assert.Equal(beforeKeepOff, learner.Estimate(keepOff).Understanding, 12);
            Assert.Equal(beforePlease, learner.Estimate(please).Understanding, 12);
        }

        [Fact]
        public async Task SuccessfulCompletionFlagDoesNotInventEvidence()
        {
            var learner = CreateLearner();
            var plan = await BuildPipeline(learner).PlanAsync(Source, AssistancePolicy.ForMode(AssistanceMode.Balanced));
            var keepOff = GetUnit(plan, "keep off", SemanticUnitKind.MultiwordExpression);
            var beforeKeepOff = learner.Estimate(keepOff).Understanding;
            var session = new LearningEncounterSession(plan, new LearnerAdaptationEngine(learner));

            var summary = session.Finish(successfulUnassistedCompletion: true);

            Assert.True(summary.SuccessfulUnassistedCompletion);
            Assert.Empty(summary.Updates);
            Assert.Equal(beforeKeepOff, learner.Estimate(keepOff).Understanding, 12);
        }

        [Fact]
        public async Task VerifiedUnaidedSuccessMustBeSpecificAndUnassisted()
        {
            var learner = CreateLearner();
            var plan = await BuildPipeline(learner).PlanAsync(Source, AssistancePolicy.ForMode(AssistanceMode.Balanced));
            var please = GetUnit(plan, "Please", SemanticUnitKind.Word);
            var keepOff = GetUnit(plan, "keep off", SemanticUnitKind.MultiwordExpression);
            var beforePlease = learner.Estimate(please).Understanding;
            var session = new LearningEncounterSession(plan, new LearnerAdaptationEngine(learner));

            session.RecordVerifiedUnaidedSuccess(please);
            Assert.Throws<InvalidOperationException>(() => session.RecordVerifiedUnaidedSuccess(keepOff));
            var summary = session.Finish();

            var update = Assert.Single(summary.Updates);
            Assert.Equal(LearningEvidenceKind.VerifiedUnaidedSuccess, update.Evidence);
            Assert.Equal(LearningObservationOrigin.SourceDisplay, update.Origin);
            Assert.True(update.EngagementVerified);
            Assert.True(learner.Estimate(please).Understanding > beforePlease);
        }

        [Fact]
        public async Task AssistanceRequestRecordsActionThatGeneratedObservation()
        {
            var learner = CreateLearner();
            var plan = await BuildPipeline(learner).PlanAsync(Source, AssistancePolicy.ForMode(AssistanceMode.Balanced));
            var session = new LearningEncounterSession(plan, new LearnerAdaptationEngine(learner));
            var index = Source.IndexOf("keep off", StringComparison.Ordinal) + 2;
            var resolved = session.ResolveUnitAt(index);
            Assert.Equal(SemanticUnitKind.MultiwordExpression, resolved.Kind);

            session.RecordAt(index, LearningEvidenceKind.AssistanceRequested);
            var summary = session.Finish();

            var update = Assert.Single(summary.Updates);
            Assert.Equal(LearningEvidenceKind.AssistanceRequested, update.Evidence);
            Assert.Equal(LearningObservationOrigin.AssistedDisplay, update.Origin);
            Assert.True(update.UpdatedUnderstanding < update.PreviousUnderstanding);
        }

        [Fact]
        public async Task ExplicitObservationOriginCannotContradictEncounterDisplayAction()
        {
            var learner = CreateLearner();
            var plan = await BuildPipeline(learner).PlanAsync(Source, AssistancePolicy.ForMode(AssistanceMode.Balanced));
            var keepOff = GetUnit(plan, "keep off", SemanticUnitKind.MultiwordExpression);
            var please = GetUnit(plan, "Please", SemanticUnitKind.Word);
            var session = new LearningEncounterSession(plan, new LearnerAdaptationEngine(learner));

            Assert.Throws<InvalidOperationException>(() =>
                session.Record(
                    keepOff,
                    LearningEvidenceKind.AssistanceRequested,
                    LearningObservationOrigin.SourceDisplay,
                    engagementVerified: true));

            Assert.Throws<InvalidOperationException>(() =>
                session.Record(
                    please,
                    LearningEvidenceKind.VerifiedUnaidedSuccess,
                    LearningObservationOrigin.AssistedDisplay,
                    engagementVerified: true));
        }

        [Fact]
        public async Task GenericVerifiedUnaidedEvidenceCannotBypassInterventionCensoring()
        {
            var learner = CreateLearner();
            var plan = await BuildPipeline(learner).PlanAsync(Source, AssistancePolicy.ForMode(AssistanceMode.Balanced));
            var keepOff = GetUnit(plan, "keep off", SemanticUnitKind.MultiwordExpression);
            var session = new LearningEncounterSession(plan, new LearnerAdaptationEngine(learner));

            Assert.Throws<InvalidOperationException>(() =>
                session.Record(keepOff, LearningEvidenceKind.VerifiedUnaidedSuccess));
        }

        [Fact]
        public async Task CurrentDisplayStaysFrozenWhileNextEncounterUsesExplicitEvidence()
        {
            var learner = CreateLearner();
            var pipeline = BuildPipeline(learner);
            var policy = AssistancePolicy.ForMode(AssistanceMode.Balanced);
            var current = await pipeline.PlanAsync(Source, policy);
            Assert.Equal("Please 立ち入らない the grass.", current.DisplayText);
            var session = new LearningEncounterSession(current, new LearnerAdaptationEngine(learner));
            var keepOffIndex = Source.IndexOf("keep off", StringComparison.Ordinal) + 1;

            session.RecordAt(keepOffIndex, LearningEvidenceKind.MarkedKnown);
            session.Finish();

            Assert.Equal("Please 立ち入らない the grass.", current.DisplayText);
            var next = await pipeline.PlanAsync(Source, policy);
            Assert.Equal(Source, next.DisplayText);
        }

        [Fact]
        public async Task FinishIsIdempotentAndRejectsLaterEvidence()
        {
            var learner = CreateLearner();
            var plan = await BuildPipeline(learner).PlanAsync(Source, AssistancePolicy.ForMode(AssistanceMode.Balanced));
            var session = new LearningEncounterSession(plan, new LearnerAdaptationEngine(learner));
            var first = session.Finish();
            var afterFirst = learner.Estimate(GetUnit(plan, "keep off", SemanticUnitKind.MultiwordExpression)).Understanding;

            var second = session.Finish();
            var afterSecond = learner.Estimate(GetUnit(plan, "keep off", SemanticUnitKind.MultiwordExpression)).Understanding;

            Assert.Same(first, second);
            Assert.Equal(afterFirst, afterSecond, 12);
            Assert.Throws<InvalidOperationException>(() =>
                session.RecordAt(Source.IndexOf("keep off", StringComparison.Ordinal), LearningEvidenceKind.MarkedUnknown));
        }

        [Fact]
        public void LegacyPlanWithoutSemanticDocumentCannotStartLearningSession()
        {
            var assistance = new AssistancePlan(Array.Empty<AssistanceDecision>(), 0.0, 0.0);
            var plan = new MixedLanguagePlan(
                "hello",
                new[] { new MixedLanguageSegment("hello", "hello", false, null) },
                assistance);
            var learner = new InMemoryLearnerModel();

            Assert.Throws<ArgumentException>(() =>
                new LearningEncounterSession(plan, new LearnerAdaptationEngine(learner)));
        }

        private const string Source = "Please keep off the grass.";

        private static InMemoryLearnerModel CreateLearner()
        {
            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding("keep off", 0.10);
            return learner;
        }

        private static LanguagePipeline BuildPipeline(InMemoryLearnerModel learner)
        {
            return new LanguagePipeline(
                new RuleBasedSemanticSegmenter(new[] { "keep off" }),
                learner,
                new AssistancePlanner(),
                new DictionaryTranslationEngine(new Dictionary<string, string>
                {
                    ["keep off"] = "立ち入らない"
                }));
        }

        private static SemanticUnit GetUnit(MixedLanguagePlan plan, string text, SemanticUnitKind kind)
        {
            Assert.NotNull(plan.Document);
            return Assert.Single(plan.Document!.OfKind(kind), unit =>
                string.Equals(unit.Text, text, StringComparison.OrdinalIgnoreCase));
        }
    }
}
