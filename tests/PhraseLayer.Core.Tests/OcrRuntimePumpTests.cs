using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class OcrRuntimePumpTests
    {
        [Fact]
        public async Task SuccessfulRunCapturesRecognizesAndPresentsExactFrame()
        {
            var frame = Frame(1_000_000);
            var stream = new QueueCameraStream(frame);
            var engine = new RecordingOcrEngine(new OcrObservation("exit", 0.9));
            var sink = new RecordingSink();
            var pump = CreatePump(stream, engine, sink, maxInferencesPerSecond: 5.0);

            var result = await pump.TryRunOnceAsync();

            Assert.Equal(OcrPumpStatus.Presented, result.Status);
            Assert.Equal(CameraCaptureState.Ready, result.CameraState);
            Assert.Equal(1_000_000, result.FrameTimestampMicroseconds);
            Assert.Equal(OcrScheduleStatus.Processed, result.ScheduleStatus);
            Assert.True(result.Presented);
            Assert.Same(frame, engine.LastFrame);
            Assert.Same(frame, sink.Frame);
            Assert.Equal("exit", sink.Observation?.Text);
        }

        [Fact]
        public async Task CameraUnavailableDoesNotInvokeOcrOrPresenter()
        {
            var stream = new QueueCameraStream((ImageFrame?)null);
            var engine = new RecordingOcrEngine(new OcrObservation("unused", 1.0));
            var sink = new RecordingSink();
            var pump = CreatePump(stream, engine, sink, maxInferencesPerSecond: 5.0);

            var result = await pump.TryRunOnceAsync();

            Assert.Equal(OcrPumpStatus.CameraUnavailable, result.Status);
            Assert.Equal(CameraCaptureState.Failed, result.CameraState);
            Assert.Equal(0, engine.CallCount);
            Assert.Equal(0, sink.CallCount);
        }

        [Fact]
        public async Task RateLimitedFrameDoesNotReplaceLastPresentation()
        {
            var first = Frame(1_000_000);
            var second = Frame(1_100_000);
            var stream = new QueueCameraStream(first, second);
            var engine = new RecordingOcrEngine(new OcrObservation("stable", 0.9));
            var sink = new RecordingSink();
            var pump = CreatePump(stream, engine, sink, maxInferencesPerSecond: 1.0);

            var firstResult = await pump.TryRunOnceAsync();
            var secondResult = await pump.TryRunOnceAsync();

            Assert.Equal(OcrPumpStatus.Presented, firstResult.Status);
            Assert.Equal(OcrPumpStatus.SkippedRateLimit, secondResult.Status);
            Assert.Equal(OcrScheduleStatus.SkippedRateLimit, secondResult.ScheduleStatus);
            Assert.False(secondResult.Presented);
            Assert.Equal(1, engine.CallCount);
            Assert.Equal(1, sink.CallCount);
            Assert.Same(first, sink.Frame);
        }

        [Fact]
        public async Task OverlappingRunIsRejectedBeforeAnotherCapture()
        {
            var first = Frame(1_000_000);
            var second = Frame(2_000_000);
            var stream = new QueueCameraStream(first, second);
            var engine = new BlockingOcrEngine();
            var sink = new RecordingSink();
            var pump = CreatePump(stream, engine, sink, maxInferencesPerSecond: 5.0);

            var activeRun = pump.TryRunOnceAsync();
            await engine.Entered.Task;

            var overlapping = await pump.TryRunOnceAsync();

            Assert.Equal(OcrPumpStatus.SkippedPumpBusy, overlapping.Status);
            Assert.Equal(1, stream.CaptureCount);
            Assert.Equal(1, engine.CallCount);

            engine.Complete(new OcrObservation("done", 1.0));
            var completed = await activeRun;
            Assert.Equal(OcrPumpStatus.Presented, completed.Status);
        }

        [Fact]
        public async Task CancellationReleasesPumpAndSchedulerForNextRun()
        {
            var first = Frame(1_000_000);
            var second = Frame(2_000_000);
            var stream = new QueueCameraStream(first, second);
            var engine = new CancelFirstOcrEngine(new OcrObservation("recovered", 0.95));
            var sink = new RecordingSink();
            var pump = CreatePump(stream, engine, sink, maxInferencesPerSecond: 5.0);
            using var cancellation = new CancellationTokenSource();

            var cancelledRun = pump.TryRunOnceAsync(cancellation.Token);
            await engine.FirstCallEntered.Task;
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRun);

            var recovered = await pump.TryRunOnceAsync();
            Assert.Equal(OcrPumpStatus.Presented, recovered.Status);
            Assert.Equal(2, stream.CaptureCount);
            Assert.Equal(2, engine.CallCount);
            Assert.Equal(1, sink.CallCount);
            Assert.Same(second, sink.Frame);
        }

        private static OcrRuntimePump CreatePump(
            ICameraStreamBackend stream,
            IOcrEngine engine,
            IOcrObservationSink sink,
            double maxInferencesPerSecond)
        {
            var camera = new CameraCaptureCoordinator(new GrantedPermission(), stream);
            var scheduler = new OcrFrameScheduler(engine, maxInferencesPerSecond);
            var presenter = new OcrPresentationCoordinator(sink);
            return new OcrRuntimePump(camera, scheduler, presenter);
        }

        private static ImageFrame Frame(long timestamp)
        {
            return new ImageFrame(new byte[4], 10, 10, timestamp);
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

        private sealed class QueueCameraStream : ICameraStreamBackend
        {
            private readonly Queue<ImageFrame?> frames;

            public QueueCameraStream(params ImageFrame?[] frames)
            {
                this.frames = new Queue<ImageFrame?>(frames);
            }

            public bool IsPlaying { get; private set; } = true;
            public int CaptureCount { get; private set; }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IsPlaying = true;
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IsPlaying = false;
                return Task.CompletedTask;
            }

            public Task<ImageFrame?> CaptureAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CaptureCount++;
                return Task.FromResult(frames.Count == 0 ? null : frames.Dequeue());
            }
        }

        private sealed class RecordingOcrEngine : IOcrEngine
        {
            private readonly OcrObservation observation;

            public RecordingOcrEngine(OcrObservation observation)
            {
                this.observation = observation;
            }

            public int CallCount { get; private set; }
            public ImageFrame? LastFrame { get; private set; }

            public Task<OcrObservation> RecognizeAsync(ImageFrame frame, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                LastFrame = frame;
                return Task.FromResult(observation);
            }
        }

        private sealed class BlockingOcrEngine : IOcrEngine
        {
            private readonly TaskCompletionSource<OcrObservation> completion =
                new TaskCompletionSource<OcrObservation>(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<bool> Entered { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public int CallCount { get; private set; }

            public Task<OcrObservation> RecognizeAsync(ImageFrame frame, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                Entered.TrySetResult(true);
                return completion.Task;
            }

            public void Complete(OcrObservation observation)
            {
                completion.TrySetResult(observation);
            }
        }

        private sealed class CancelFirstOcrEngine : IOcrEngine
        {
            private readonly OcrObservation recoveredObservation;

            public CancelFirstOcrEngine(OcrObservation recoveredObservation)
            {
                this.recoveredObservation = recoveredObservation;
            }

            public TaskCompletionSource<bool> FirstCallEntered { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public int CallCount { get; private set; }

            public async Task<OcrObservation> RecognizeAsync(ImageFrame frame, CancellationToken cancellationToken = default)
            {
                CallCount++;
                if (CallCount == 1)
                {
                    FirstCallEntered.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return recoveredObservation;
            }
        }

        private sealed class RecordingSink : IOcrObservationSink
        {
            public int CallCount { get; private set; }
            public OcrObservation? Observation { get; private set; }
            public ImageFrame? Frame { get; private set; }

            public void Present(OcrObservation observation, ImageFrame frame)
            {
                CallCount++;
                Observation = observation;
                Frame = frame;
            }
        }
    }
}
