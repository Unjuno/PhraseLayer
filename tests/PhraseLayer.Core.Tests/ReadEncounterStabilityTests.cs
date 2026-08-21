using System.Collections.Generic;
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
    public sealed class ReadEncounterStabilityTests
    {
        [Fact]
        public async Task SameEncounterKeepsFrozenPlanAfterLearnerBeliefChanges()
        {
            var learner = BuildLearner();
            var pipeline = BuildPipeline(learner);
            var firstObservation = KeepOffObservation(100, 100);
            var first = await pipeline.ProcessAsync(
                Frame(100_000),
                firstObservation,
                AssistancePolicy.ForMode(AssistanceMode.Challenge));

            Assert.Equal(ReadEncounterTransition.Started, first.Decision.Transition);
            Assert.Contains("立ち入らない", first.SpatialResult.LanguagePlan.DisplayText);

            // Evidence acquired during an encounter may change the learner model, but it must affect only a
            // future encounter. The currently visible source/translation mix remains frozen.
            learner.SetUnderstanding("keep off", 0.99);
            var second = await pipeline.ProcessAsync(
                Frame(200_000),
                KeepOffObservation(125, 108),
                AssistancePolicy.ForMode(AssistanceMode.Challenge));

            Assert.Equal(ReadEncounterTransition.Continued, second.Decision.Transition);
            Assert.Equal(first.Decision.EncounterId, second.Decision.EncounterId);
            Assert.Same(first.SpatialResult.LanguagePlan, second.SpatialResult.LanguagePlan);
            Assert.Contains("立ち入らない", second.SpatialResult.LanguagePlan.DisplayText);
            Assert.True(second.SpatialResult.SpatialAssistance.ExactCount > 0);
        }

        [Fact]
        public async Task OneContradictoryObservationDoesNotSwitchEncounter()
        {
            var pipeline = BuildPipeline(BuildLearner());
            var first = await pipeline.ProcessAsync(
                Frame(100_000),
                KeepOffObservation(100, 100),
                AssistancePolicy.ForMode(AssistanceMode.Challenge));

            var noisy = await pipeline.ProcessAsync(
                Frame(200_000),
                EmergencyExitObservation(650, 350),
                AssistancePolicy.ForMode(AssistanceMode.Challenge));

            Assert.Equal(ReadEncounterTransition.PendingSwitch, noisy.Decision.Transition);
            Assert.Equal(first.Decision.EncounterId, noisy.Decision.EncounterId);
            Assert.Same(first.SpatialResult.LanguagePlan, noisy.SpatialResult.LanguagePlan);
            Assert.Equal("Please keep off the grass.", noisy.SpatialResult.LanguagePlan.SourceText);
        }

        [Fact]
        public async Task RepeatedContradictoryObservationConfirmsNewEncounter()
        {
            var pipeline = BuildPipeline(BuildLearner());
            var first = await pipeline.ProcessAsync(
                Frame(100_000),
                KeepOffObservation(100, 100),
                AssistancePolicy.ForMode(AssistanceMode.Challenge));

            var pending = await pipeline.ProcessAsync(
                Frame(200_000),
                EmergencyExitObservation(650, 350),
                AssistancePolicy.ForMode(AssistanceMode.Challenge));
            var switched = await pipeline.ProcessAsync(
                Frame(300_000),
                EmergencyExitObservation(660, 355),
                AssistancePolicy.ForMode(AssistanceMode.Challenge));

            Assert.Equal(ReadEncounterTransition.PendingSwitch, pending.Decision.Transition);
            Assert.Equal(ReadEncounterTransition.Switched, switched.Decision.Transition);
            Assert.NotEqual(first.Decision.EncounterId, switched.Decision.EncounterId);
            Assert.Equal(first.Decision.EncounterId, switched.Decision.PreviousEncounterId);
            Assert.Equal("Emergency exit.", switched.SpatialResult.LanguagePlan.SourceText);
            Assert.Contains("非常口", switched.SpatialResult.LanguagePlan.DisplayText);
        }

        [Fact]
        public async Task LongGapStartsFreshEncounterEvenForSameText()
        {
            var pipeline = BuildPipeline(BuildLearner());
            var first = await pipeline.ProcessAsync(
                Frame(100_000),
                KeepOffObservation(100, 100),
                AssistancePolicy.ForMode(AssistanceMode.Challenge));
            var later = await pipeline.ProcessAsync(
                Frame(3_100_001),
                KeepOffObservation(100, 100),
                AssistancePolicy.ForMode(AssistanceMode.Challenge));

            Assert.Equal(ReadEncounterTransition.RestartedAfterGap, later.Decision.Transition);
            Assert.NotEqual(first.Decision.EncounterId, later.Decision.EncounterId);
        }

        [Fact]
        public async Task StaleFrameCannotRollEncounterIdentityBackward()
        {
            var pipeline = BuildPipeline(BuildLearner());
            var first = await pipeline.ProcessAsync(
                Frame(200_000),
                KeepOffObservation(100, 100),
                AssistancePolicy.ForMode(AssistanceMode.Challenge));
            var stale = await pipeline.ProcessAsync(
                Frame(150_000),
                EmergencyExitObservation(650, 350),
                AssistancePolicy.ForMode(AssistanceMode.Challenge));

            Assert.Equal(ReadEncounterTransition.IgnoredStaleObservation, stale.Decision.Transition);
            Assert.True(stale.Decision.IsStale);
            Assert.Equal(first.Decision.EncounterId, stale.Decision.EncounterId);
            Assert.Same(first.SpatialResult.LanguagePlan, stale.SpatialResult.LanguagePlan);
        }

        private static InMemoryLearnerModel BuildLearner()
        {
            var learner = new InMemoryLearnerModel(0.99);
            learner.SetUnderstanding("keep off", 0.05);
            learner.SetUnderstanding("emergency exit", 0.05);
            return learner;
        }

        private static ReadEncounterPipeline BuildPipeline(InMemoryLearnerModel learner)
        {
            var language = new LanguagePipeline(
                new RuleBasedSemanticSegmenter(new[] { "keep off", "emergency exit" }),
                learner,
                new AssistancePlanner(),
                new DictionaryTranslationEngine(new Dictionary<string, string>
                {
                    ["keep off"] = "立ち入らない",
                    ["emergency exit"] = "非常口"
                }));
            return new ReadEncounterPipeline(language);
        }

        private static ImageFrame Frame(long timestampMicroseconds)
        {
            return new ImageFrame(new byte[4], 1000, 600, timestampMicroseconds);
        }

        private static OcrObservation KeepOffObservation(double x, double y)
        {
            return new OcrObservation(
                "Please keep off the grass.",
                0.98,
                new[]
                {
                    Region("Please", x, y, 100, 50),
                    Region("keep", x + 115, y, 100, 50),
                    Region("off", x + 230, y, 80, 50),
                    Region("the", x + 325, y, 70, 50),
                    Region("grass", x + 410, y, 110, 50)
                });
        }

        private static OcrObservation EmergencyExitObservation(double x, double y)
        {
            return new OcrObservation(
                "Emergency exit.",
                0.97,
                new[] { Region("Emergency exit", x, y, 220, 70) });
        }

        private static OcrRegion Region(string text, double x, double y, double width, double height)
        {
            return new OcrRegion(text, 0.99, ImageQuad.FromRect(x, y, width, height));
        }
    }
}
