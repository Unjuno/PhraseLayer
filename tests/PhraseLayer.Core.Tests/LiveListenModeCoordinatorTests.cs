using System;
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
    public sealed class LiveListenModeCoordinatorTests
    {
        [Fact]
        public async Task PartialTranscriptIsExposedWithoutTranslationByDefault()
        {
            var processor = new ListenModeObservationProcessor(
                new FakeAsrEngine("Please keep", isFinal: false),
                BuildLanguage());

            var result = await processor.ProcessAsync(
                new AudioChunk(new float[160], 16000, 1),
                AssistancePolicy.ForMode(AssistanceMode.Balanced));

            Assert.Equal("Please keep", result.Observation.Text);
            Assert.False(result.Observation.IsFinal);
            Assert.Null(result.LanguagePlan);
        }

        [Fact]
        public async Task FinalTranscriptFlowsIntoAdaptiveLanguagePipeline()
        {
            var processor = new ListenModeObservationProcessor(
                new FakeAsrEngine("Please keep off the grass.", isFinal: true),
                BuildLanguage());

            var result = await processor.ProcessAsync(
                new AudioChunk(new float[160], 16000, 1),
                AssistancePolicy.ForMode(AssistanceMode.Balanced));

            Assert.True(result.Observation.IsFinal);
            Assert.NotNull(result.LanguagePlan);
            Assert.Equal("Please 立ち入らない the grass.", result.LanguagePlan!.DisplayText);
        }

        [Fact]
        public async Task PartialTranscriptCanBeExplicitlyPlanned()
        {
            var processor = new ListenModeObservationProcessor(
                new FakeAsrEngine("Please keep off", isFinal: false),
                BuildLanguage(),
                planPartialObservations: true);

            var result = await processor.ProcessAsync(
                new AudioChunk(new float[160], 16000, 1),
                AssistancePolicy.ForMode(AssistanceMode.Balanced));

            Assert.NotNull(result.LanguagePlan);
            Assert.Equal(result.Observation.Text, result.LanguagePlan!.SourceText);
            Assert.Equal("Please keep off", result.LanguagePlan.SourceText);
        }

        [Fact]
        public async Task CoordinatorRejectsEqualOrOlderAudioTimestamp()
        {
            using var coordinator = new LiveListenModeCoordinator(
                new ListenModeObservationProcessor(
                    new FakeAsrEngine("Please keep off the grass."),
                    BuildLanguage()));
            var policy = AssistancePolicy.ForMode(AssistanceMode.Balanced);

            var first = await coordinator.SubmitAsync(
                new AudioChunk(new float[160], 16000, 10), policy);
            var equal = await coordinator.SubmitAsync(
                new AudioChunk(new float[160], 16000, 10), policy);
            var older = await coordinator.SubmitAsync(
                new AudioChunk(new float[160], 16000, 9), policy);

            Assert.Equal(LiveListenModeProcessingStatus.Processed, first.Status);
            Assert.Equal(LiveListenModeProcessingStatus.StaleInput, equal.Status);
            Assert.Equal(LiveListenModeProcessingStatus.StaleInput, older.Status);
        }

        [Fact]
        public async Task NewerUtteranceCancelsOlderInFlightRecognition()
        {
            var asr = new FirstCallBlocksUntilCancelledAsrEngine();
            using var coordinator = new LiveListenModeCoordinator(
                new ListenModeObservationProcessor(asr, BuildLanguage()));
            var policy = AssistancePolicy.ForMode(AssistanceMode.Balanced);

            var olderTask = coordinator.SubmitAsync(
                new AudioChunk(new float[160], 16000, 1), policy);
            await asr.FirstCallStarted.Task;

            var newer = await coordinator.SubmitAsync(
                new AudioChunk(new float[160], 16000, 2), policy);
            var older = await olderTask;

            Assert.Equal(LiveListenModeProcessingStatus.Processed, newer.Status);
            Assert.Equal(LiveListenModeProcessingStatus.Superseded, older.Status);
            Assert.Equal("Please 立ち入らない the grass.", newer.Output!.LanguagePlan!.DisplayText);
        }

        private static LanguagePipeline BuildLanguage()
        {
            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding("keep off", 0.10);
            return new LanguagePipeline(
                new RuleBasedSemanticSegmenter(new[] { "keep off" }),
                learner,
                new AssistancePlanner(),
                new DictionaryTranslationEngine(new Dictionary<string, string>
                {
                    ["keep off"] = "立ち入らない"
                }));
        }

        private sealed class FirstCallBlocksUntilCancelledAsrEngine : IAsrEngine
        {
            private int callCount;

            public TaskCompletionSource<bool> FirstCallStarted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<AsrObservation> TranscribeAsync(
                AudioChunk audio,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                var call = Interlocked.Increment(ref callCount);
                if (call == 1)
                {
                    FirstCallStarted.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                return new AsrObservation("Please keep off the grass.", true);
            }
        }
    }
}
