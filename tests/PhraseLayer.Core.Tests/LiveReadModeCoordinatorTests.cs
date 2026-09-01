using System;
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
    public sealed class LiveReadModeCoordinatorTests
    {
        [Fact]
        public async Task NewerObservationSupersedesOlderInFlightResult()
        {
            var translator = new BlockingFirstTranslationEngine();
            using var coordinator = CreateCoordinator(translator);
            var policy = AssistancePolicy.ForMode(AssistanceMode.Easy);

            var firstTask = coordinator.SubmitAsync(
                Frame(1_000_000),
                new OcrObservation("hello", 0.99),
                policy);
            await translator.FirstCallEntered.Task;

            var second = await coordinator.SubmitAsync(
                Frame(2_000_000),
                new OcrObservation("world", 0.99),
                policy);

            Assert.Equal(LiveReadModeProcessingStatus.Processed, second.Status);
            Assert.NotNull(second.Aligned);
            Assert.Contains("新", second.Aligned!.Spatial.LanguagePlan.DisplayText);

            translator.CompleteFirst("旧");
            var first = await firstTask;

            Assert.Equal(LiveReadModeProcessingStatus.Superseded, first.Status);
            Assert.Null(first.Aligned);
            Assert.Equal(2_000_000, coordinator.LatestAcceptedTimestampMicroseconds);
        }

        [Fact]
        public async Task OlderTimestampIsRejectedBeforeLanguageProcessing()
        {
            var translator = new CountingTranslationEngine();
            using var coordinator = CreateCoordinator(translator);
            var policy = AssistancePolicy.ForMode(AssistanceMode.Easy);

            var first = await coordinator.SubmitAsync(
                Frame(2_000_000),
                new OcrObservation("hello", 0.99),
                policy);
            var callsAfterFirst = translator.CallCount;

            var stale = await coordinator.SubmitAsync(
                Frame(1_000_000),
                new OcrObservation("older", 0.99),
                policy);

            Assert.Equal(LiveReadModeProcessingStatus.Processed, first.Status);
            Assert.Equal(LiveReadModeProcessingStatus.StaleInput, stale.Status);
            Assert.Null(stale.Aligned);
            Assert.Equal(callsAfterFirst, translator.CallCount);
        }

        [Fact]
        public async Task EqualTimestampIsRejectedAsStale()
        {
            var translator = new CountingTranslationEngine();
            using var coordinator = CreateCoordinator(translator);
            var policy = AssistancePolicy.ForMode(AssistanceMode.Easy);

            await coordinator.SubmitAsync(
                Frame(3_000_000),
                new OcrObservation("hello", 0.99),
                policy);
            var callsAfterFirst = translator.CallCount;

            var duplicate = await coordinator.SubmitAsync(
                Frame(3_000_000),
                new OcrObservation("hello", 0.99),
                policy);

            Assert.Equal(LiveReadModeProcessingStatus.StaleInput, duplicate.Status);
            Assert.Equal(callsAfterFirst, translator.CallCount);
        }

        [Fact]
        public async Task ExternalCancellationPropagates()
        {
            var translator = new CancellableTranslationEngine();
            using var coordinator = CreateCoordinator(translator);
            using var cancellation = new CancellationTokenSource();

            var task = coordinator.SubmitAsync(
                Frame(4_000_000),
                new OcrObservation("hello", 0.99),
                AssistancePolicy.ForMode(AssistanceMode.Easy),
                cancellation.Token);
            await translator.Entered.Task;
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        [Fact]
        public async Task SupersedingObservationDoesNotDisposeOlderCancellationSourceWhileAdapterUnwinds()
        {
            var translator = new CancellationRegistrationAfterCancellationTranslationEngine();
            using var coordinator = CreateCoordinator(translator);
            var policy = AssistancePolicy.ForMode(AssistanceMode.Easy);

            var olderTask = coordinator.SubmitAsync(
                Frame(5_000_000),
                new OcrObservation("hello", 0.99),
                policy);
            await translator.FirstCallEntered.Task;

            var newer = await coordinator.SubmitAsync(
                Frame(6_000_000),
                new OcrObservation("world", 0.99),
                policy);
            var older = await olderTask;

            Assert.Equal(LiveReadModeProcessingStatus.Processed, newer.Status);
            Assert.Equal(LiveReadModeProcessingStatus.Superseded, older.Status);
            Assert.True(translator.RegisteredAfterCancellation);
        }

        private static LiveReadModeCoordinator CreateCoordinator(ITranslationEngine translator)
        {
            var language = new LanguagePipeline(
                new RuleBasedSemanticSegmenter(),
                new InMemoryLearnerModel(0.0),
                new AssistancePlanner(),
                translator);
            return new LiveReadModeCoordinator(new ReadModeObservationProcessor(language));
        }

        private static ImageFrame Frame(long timestampMicroseconds)
        {
            return new ImageFrame(new byte[4], 10, 10, timestampMicroseconds);
        }

        private sealed class BlockingFirstTranslationEngine : ITranslationEngine
        {
            private readonly TaskCompletionSource<string> firstCompletion =
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<bool> FirstCallEntered { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            private int callCount;

            public Task<string> TranslateAsync(
                string sourceText,
                string context,
                CancellationToken cancellationToken = default)
            {
                callCount++;
                if (callCount == 1)
                {
                    FirstCallEntered.TrySetResult(true);
                    return firstCompletion.Task;
                }
                return Task.FromResult("新");
            }

            public void CompleteFirst(string value)
            {
                firstCompletion.TrySetResult(value);
            }
        }

        private sealed class CountingTranslationEngine : ITranslationEngine
        {
            public int CallCount { get; private set; }

            public Task<string> TranslateAsync(
                string sourceText,
                string context,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                return Task.FromResult("訳");
            }
        }

        private sealed class CancellableTranslationEngine : ITranslationEngine
        {
            public TaskCompletionSource<bool> Entered { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<string> TranslateAsync(
                string sourceText,
                string context,
                CancellationToken cancellationToken = default)
            {
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return "unused";
            }
        }

        private sealed class CancellationRegistrationAfterCancellationTranslationEngine : ITranslationEngine
        {
            private int callCount;

            public TaskCompletionSource<bool> FirstCallEntered { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool RegisteredAfterCancellation { get; private set; }

            public async Task<string> TranslateAsync(
                string sourceText,
                string context,
                CancellationToken cancellationToken = default)
            {
                var call = Interlocked.Increment(ref callCount);
                if (call == 1)
                {
                    FirstCallEntered.TrySetResult(true);
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

                return "新";
            }
        }
    }
}
