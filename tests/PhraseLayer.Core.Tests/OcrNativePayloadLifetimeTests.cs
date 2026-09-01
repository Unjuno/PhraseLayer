using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class OcrNativePayloadLifetimeTests
    {
        [Fact]
        public async Task ProcessedFrameReleasesNativeResourceAfterPresentation()
        {
            var payload = new ReleasablePayload();
            var frame = new ImageFrame(payload, 32, 24, 1_000_000);
            var sink = new RecordingSink();
            var pump = CreatePump(new QueueStream(frame), new ImmediateEngine(), sink, 5.0);

            var result = await pump.TryRunOnceAsync();

            Assert.True(result.Presented);
            Assert.Same(frame, sink.Frame);
            Assert.Equal(1, payload.ReleaseCount);
        }

        [Fact]
        public async Task RateLimitedFrameReleasesResourceWithoutSecondInference()
        {
            var firstPayload = new ReleasablePayload();
            var secondPayload = new ReleasablePayload();
            var engine = new ImmediateEngine();
            var pump = CreatePump(
                new QueueStream(
                    new ImageFrame(firstPayload, 32, 24, 1_000_000),
                    new ImageFrame(secondPayload, 32, 24, 1_100_000)),
                engine,
                new RecordingSink(),
                1.0);

            await pump.TryRunOnceAsync();
            var second = await pump.TryRunOnceAsync();

            Assert.Equal(OcrPumpStatus.SkippedRateLimit, second.Status);
            Assert.Equal(1, engine.CallCount);
            Assert.Equal(1, firstPayload.ReleaseCount);
            Assert.Equal(1, secondPayload.ReleaseCount);
        }

        [Fact]
        public async Task CancelledInferenceStillReleasesNativeResource()
        {
            var payload = new ReleasablePayload();
            var engine = new BlockingEngine();
            var pump = CreatePump(
                new QueueStream(new ImageFrame(payload, 32, 24, 1_000_000)),
                engine,
                new RecordingSink(),
                5.0);
            using var cancellation = new CancellationTokenSource();

            var active = pump.TryRunOnceAsync(cancellation.Token);
            await engine.Entered.Task;
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            Assert.Equal(1, payload.ReleaseCount);
        }

        private static OcrRuntimePump CreatePump(
            ICameraStreamBackend stream,
            IOcrEngine engine,
            IOcrObservationSink sink,
            double maxHz)
        {
            return new OcrRuntimePump(
                new CameraCaptureCoordinator(new GrantedPermission(), stream),
                new OcrFrameScheduler(engine, maxHz),
                new OcrPresentationCoordinator(sink));
        }

        private sealed class ReleasablePayload : IReleasableImageFramePayload
        {
            public int ReleaseCount { get; private set; }
            public void ReleaseImageResource() => ReleaseCount++;
        }

        private sealed class GrantedPermission : ICameraPermissionService
        {
            public CameraPermissionState State => CameraPermissionState.Granted;
            public Task<CameraPermissionState> RequestAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(CameraPermissionState.Granted);
            }
        }

        private sealed class QueueStream : ICameraStreamBackend
        {
            private readonly Queue<ImageFrame> frames;
            public QueueStream(params ImageFrame[] frames) => this.frames = new Queue<ImageFrame>(frames);
            public bool IsPlaying => true;
            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<ImageFrame> CaptureAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(frames.Count == 0 ? null : frames.Dequeue());
            }
        }

        private sealed class ImmediateEngine : IOcrEngine
        {
            public int CallCount { get; private set; }
            public Task<OcrObservation> RecognizeAsync(ImageFrame frame, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                return Task.FromResult(new OcrObservation("exit", 1.0));
            }
        }

        private sealed class BlockingEngine : IOcrEngine
        {
            public TaskCompletionSource<bool> Entered { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<OcrObservation> RecognizeAsync(ImageFrame frame, CancellationToken cancellationToken = default)
            {
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new OcrObservation("unreachable", 1.0);
            }
        }

        private sealed class RecordingSink : IOcrObservationSink
        {
            public ImageFrame Frame { get; private set; }
            public void Present(OcrObservation observation, ImageFrame frame) => Frame = frame;
        }
    }
}
