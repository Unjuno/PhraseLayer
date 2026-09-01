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
            Assert.True(result.Timings.AsrMilliseconds >= 0.0);
            Assert.Equal(0.0, result.Timings.LanguagePlanMilliseconds);
            Assert.True(result.Timings.TotalMilliseconds >= result.Timings.AsrMilliseconds);
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
            Assert.True(result.Timings.AsrMilliseconds >= 0.0);
            Assert.True(result.Timings.LanguagePlanMilliseconds >= 0.0);
            Assert.True(result.Timings.TotalMilliseconds >= result.Timings.AsrMilliseconds);
            Assert.True(result.Timings.TotalMilliseconds >= result.Timings.LanguagePlanMilliseconds);
        }

        [Fact]
        public async Task TimingsSeparateAsrAndAdaptiveLanguagePlanning()
        {
            var processor = new ListenModeObservationProcessor(
                new DelayedAsrEngine("Please keep off the grass.", 15),
                BuildLanguage(new DelayedTranslationEngine(15)));

            var result = await processor.ProcessAsync(
                new AudioChunk(new float[160], 16000, 1),
                AssistancePolicy.ForMode(AssistanceMode.Balanced));

            Assert.NotNull(result.LanguagePlan);
            Assert.True(result.Timings.AsrMilliseconds >= 5.0);
            Assert.True(result.Timings.LanguagePlanMilliseconds >= 5.0);
            Assert.True(
                result.Timings.TotalMilliseconds + 1.0 >=
                result.Timings.AsrMilliseconds + result.Timings.LanguagePlanMilliseconds);
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
            Assert.True(result.Timings.LanguagePlanMilliseconds >= 0.0);
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

        [Fact]
        public async Task SupersedingRequestDoesNotDisposeOlderCancellationSourceWhileAdapterUnwinds()
        {
            var asr = new CancellationRegistrationAfterCancellationAsrEngine();
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
            Assert.True(asr.RegisteredAfterCancellation);
        }

        private static LanguagePipeline BuildLanguage(ITranslationEngine? translation = null)
        {
            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding("keep off", 0.10);
            return new LanguagePipeline(
                new RuleBasedSemanticSegmenter(new[] { "keep off" }),
                learner,
                new AssistancePlanner(),
                translation ?? new DictionaryTranslationEngine(new Dictionary<string, string>
                {
                    ["keep off"] = "立ち入らない"
                }));
        }

        private sealed class DelayedAsrEngine : IAsrEngine
        {
            private readonly string text;
            private readonly int delayMilliseconds;

            public DelayedAsrEngine(string text, int delayMilliseconds)
            {
                this.text = text;
                this.delayMilliseconds = delayMilliseconds;
            }

            public async Task<AsrObservation> TranscribeAsync(
                AudioChunk audio,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                await Task.Delay(delayMilliseconds, cancellationToken);
                return new AsrObservation(text, true);
            }
        }

        private sealed class DelayedTranslationEngine : ITranslationEngine
        {
            private readonly int delayMilliseconds;

            public DelayedTranslationEngine(int delayMilliseconds)
            {
                this.delayMilliseconds = delayMilliseconds;
            }

            public async Task<string> TranslateAsync(
                string sourceText,
                string context,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                await Task.Delay(delayMilliseconds, cancellationToken);
                return string.Equals(sourceText, "keep off", StringComparison.OrdinalIgnoreCase)
                    ? "立ち入らない"
                    : sourceText;
            }
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

        private sealed class CancellationRegistrationAfterCancellationAsrEngine : IAsrEngine
        {
            private int callCount;

            public TaskCompletionSource<bool> FirstCallStarted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool RegisteredAfterCancellation { get; private set; }

            public async Task<AsrObservation> TranscribeAsync(
                AudioChunk audio,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                var call = Interlocked.Increment(ref callCount);
                if (call == 1)
                {
                    FirstCallStarted.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        using (cancellationToken.Register(() => { }))
                        {
                            RegisteredAfterCancellation = true;
                        }
                        throw;
                    }
                }

                return new AsrObservation("Please keep off the grass.", true);
            }
        }
    }
}
