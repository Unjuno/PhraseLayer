using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class OcrSchedulingTests
    {
        [Fact]
        public async Task SchedulerProcessesFirstFrameAndRateLimitsNearbyFrame()
        {
            var engine = new CountingOcrEngine();
            using var scheduler = new OcrFrameScheduler(engine, 5.0); // 200 ms

            var first = await scheduler.TryProcessAsync(FrameAt(1_000_000));
            var nearby = await scheduler.TryProcessAsync(FrameAt(1_100_000));
            var later = await scheduler.TryProcessAsync(FrameAt(1_200_000));

            Assert.Equal(OcrScheduleStatus.Processed, first.Status);
            Assert.Equal(OcrScheduleStatus.SkippedRateLimit, nearby.Status);
            Assert.Equal(OcrScheduleStatus.Processed, later.Status);
            Assert.Equal(2, engine.CallCount);
        }

        [Fact]
        public async Task SchedulerRejectsOlderFrameAfterAProcessedFrame()
        {
            var engine = new CountingOcrEngine();
            using var scheduler = new OcrFrameScheduler(engine, 10.0);

            await scheduler.TryProcessAsync(FrameAt(2_000_000));
            var stale = await scheduler.TryProcessAsync(FrameAt(1_999_999));

            Assert.Equal(OcrScheduleStatus.SkippedStale, stale.Status);
            Assert.Equal(1, engine.CallCount);
        }

        [Fact]
        public async Task SchedulerSkipsConcurrentFrameInsteadOfQueueingCameraBacklog()
        {
            var engine = new BlockingOcrEngine();
            using var scheduler = new OcrFrameScheduler(engine, 30.0);

            var firstTask = scheduler.TryProcessAsync(FrameAt(3_000_000));
            await engine.Started.Task;
            var second = await scheduler.TryProcessAsync(FrameAt(3_100_000));

            Assert.Equal(OcrScheduleStatus.SkippedBusy, second.Status);
            Assert.Equal(1, engine.CallCount);

            engine.Release.TrySetResult(true);
            var first = await firstTask;
            Assert.Equal(OcrScheduleStatus.Processed, first.Status);
        }

        [Fact]
        public async Task FailedInferenceDoesNotAdvanceProcessedTimestamp()
        {
            var engine = new FailOnceOcrEngine();
            using var scheduler = new OcrFrameScheduler(engine, 5.0);

            await Assert.ThrowsAsync<System.InvalidOperationException>(
                () => scheduler.TryProcessAsync(FrameAt(4_000_000)));

            var retry = await scheduler.TryProcessAsync(FrameAt(4_010_000));

            Assert.Equal(OcrScheduleStatus.Processed, retry.Status);
            Assert.Equal(2, engine.CallCount);
        }

        [Fact]
        public async Task ResetAllowsNewTimestampSequence()
        {
            var engine = new CountingOcrEngine();
            using var scheduler = new OcrFrameScheduler(engine, 10.0);

            await scheduler.TryProcessAsync(FrameAt(5_000_000));
            scheduler.Reset();
            var restarted = await scheduler.TryProcessAsync(FrameAt(100));

            Assert.Equal(OcrScheduleStatus.Processed, restarted.Status);
            Assert.Equal(2, engine.CallCount);
        }

        private static ImageFrame FrameAt(long timestampMicroseconds)
        {
            return new ImageFrame(new byte[] { 0 }, 1, 1, timestampMicroseconds, ImagePixelFormat.Gray8);
        }

        private sealed class CountingOcrEngine : IOcrEngine
        {
            public int CallCount { get; private set; }

            public Task<OcrObservation> RecognizeAsync(
                ImageFrame frame,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                return Task.FromResult(new OcrObservation("ok", 1.0));
            }
        }

        private sealed class BlockingOcrEngine : IOcrEngine
        {
            public TaskCompletionSource<bool> Started { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int CallCount { get; private set; }

            public async Task<OcrObservation> RecognizeAsync(
                ImageFrame frame,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                CallCount++;
                Started.TrySetResult(true);
                await Release.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new OcrObservation("ok", 1.0);
            }
        }

        private sealed class FailOnceOcrEngine : IOcrEngine
        {
            public int CallCount { get; private set; }

            public Task<OcrObservation> RecognizeAsync(
                ImageFrame frame,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                CallCount++;
                if (CallCount == 1) throw new System.InvalidOperationException("synthetic failure");
                return Task.FromResult(new OcrObservation("ok", 1.0));
            }
        }
    }
}
