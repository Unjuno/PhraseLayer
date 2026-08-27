using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
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
    public sealed class PlatformSynchronizationContextTests
    {
        [Fact]
        public void CameraOcrPumpPreservesCallerSynchronizationContext()
        {
            var result = RunOnDedicatedContext(async ownerThreadId =>
            {
                var frame = new ImageFrame(new byte[4], 10, 10, 1_000_000);
                var permission = new ThreadAffinePermissionService(ownerThreadId);
                var stream = new ThreadAffineCameraStream(ownerThreadId, frame);
                var engine = new ThreadAffineOcrEngine(
                    ownerThreadId,
                    new OcrObservation("exit", 0.95));
                var sink = new ThreadAffineOcrSink(ownerThreadId);
                var camera = new CameraCaptureCoordinator(permission, stream);
                var scheduler = new OcrFrameScheduler(engine, 5.0);
                var presenter = new OcrPresentationCoordinator(sink);
                var pump = new OcrRuntimePump(camera, scheduler, presenter);

                return await pump.TryRunOnceAsync();
            });

            Assert.Equal(OcrPumpStatus.Presented, result.Status);
            Assert.Equal(CameraCaptureState.Ready, result.CameraState);
            Assert.True(result.Presented);
        }

        [Fact]
        public void ReadModePreservesCallerSynchronizationContextBetweenOcrAndTranslation()
        {
            var result = RunOnDedicatedContext(async ownerThreadId =>
            {
                var ocr = new ThreadAffineOcrEngine(
                    ownerThreadId,
                    new OcrObservation("hello", 1.0));
                var translator = new ThreadAffineTranslationEngine(ownerThreadId);
                var language = new LanguagePipeline(
                    new RuleBasedSemanticSegmenter(Array.Empty<string>()),
                    new InMemoryLearnerModel(0.0),
                    new AssistancePlanner(),
                    translator);
                var pipeline = new ReadModePipeline(ocr, language);
                var frame = new ImageFrame(new byte[4], 10, 10, 2_000_000);

                var spatial = await pipeline.ProcessSpatialAsync(
                    frame,
                    AssistancePolicy.ForMode(AssistanceMode.Easy));
                return (spatial, translator.CallCount);
            });

            Assert.True(result.CallCount > 0);
            Assert.Contains("訳", result.spatial.LanguagePlan.DisplayText);
        }

        private static T RunOnDedicatedContext<T>(Func<int, Task<T>> operation)
        {
            T result = default!;
            Exception? failure = null;
            using var finished = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                using var context = new PumpingSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(context);
                try
                {
                    var task = operation(Thread.CurrentThread.ManagedThreadId);
                    task.ContinueWith(
                        completed => context.Post(
                            _ =>
                            {
                                try
                                {
                                    result = completed.GetAwaiter().GetResult();
                                }
                                catch (Exception exception)
                                {
                                    failure = exception;
                                }
                                finally
                                {
                                    context.Complete();
                                }
                            },
                            null),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                    context.RunOnCurrentThread();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                    finished.Set();
                }
            });
            thread.IsBackground = true;
            thread.Start();

            if (!finished.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Dedicated synchronization-context test did not complete within 10 seconds.");
            thread.Join();

            if (failure != null)
                ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }

        private static async Task<T> CompleteOnWorkerAsync<T>(T value)
        {
            await Task.Delay(10).ConfigureAwait(false);
            return value;
        }

        private sealed class PumpingSynchronizationContext : SynchronizationContext, IDisposable
        {
            private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue =
                new BlockingCollection<(SendOrPostCallback Callback, object? State)>();

            public override void Post(SendOrPostCallback d, object? state)
            {
                queue.Add((d, state));
            }

            public void RunOnCurrentThread()
            {
                foreach (var work in queue.GetConsumingEnumerable())
                    work.Callback(work.State);
            }

            public void Complete()
            {
                queue.CompleteAdding();
            }

            public void Dispose()
            {
                queue.Dispose();
            }
        }

        private sealed class ThreadAffinePermissionService : ICameraPermissionService
        {
            private readonly int ownerThreadId;
            private CameraPermissionState state = CameraPermissionState.Unknown;

            public ThreadAffinePermissionService(int ownerThreadId)
            {
                this.ownerThreadId = ownerThreadId;
            }

            public CameraPermissionState State
            {
                get
                {
                    AssertOwnerThread();
                    return state;
                }
            }

            public Task<CameraPermissionState> RequestAsync(CancellationToken cancellationToken = default)
            {
                AssertOwnerThread();
                cancellationToken.ThrowIfCancellationRequested();
                state = CameraPermissionState.Granted;
                return CompleteOnWorkerAsync(CameraPermissionState.Granted);
            }

            private void AssertOwnerThread()
            {
                Assert.Equal(ownerThreadId, Thread.CurrentThread.ManagedThreadId);
            }
        }

        private sealed class ThreadAffineCameraStream : ICameraStreamBackend
        {
            private readonly int ownerThreadId;
            private readonly ImageFrame frame;
            private bool isPlaying;

            public ThreadAffineCameraStream(int ownerThreadId, ImageFrame frame)
            {
                this.ownerThreadId = ownerThreadId;
                this.frame = frame;
            }

            public bool IsPlaying
            {
                get
                {
                    AssertOwnerThread();
                    return isPlaying;
                }
            }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                AssertOwnerThread();
                cancellationToken.ThrowIfCancellationRequested();
                isPlaying = true;
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                AssertOwnerThread();
                cancellationToken.ThrowIfCancellationRequested();
                isPlaying = false;
                return Task.CompletedTask;
            }

            public Task<ImageFrame?> CaptureAsync(CancellationToken cancellationToken = default)
            {
                AssertOwnerThread();
                cancellationToken.ThrowIfCancellationRequested();
                return CompleteOnWorkerAsync<ImageFrame?>(frame);
            }

            private void AssertOwnerThread()
            {
                Assert.Equal(ownerThreadId, Thread.CurrentThread.ManagedThreadId);
            }
        }

        private sealed class ThreadAffineOcrEngine : IOcrEngine
        {
            private readonly int ownerThreadId;
            private readonly OcrObservation observation;

            public ThreadAffineOcrEngine(int ownerThreadId, OcrObservation observation)
            {
                this.ownerThreadId = ownerThreadId;
                this.observation = observation;
            }

            public Task<OcrObservation> RecognizeAsync(
                ImageFrame frame,
                CancellationToken cancellationToken = default)
            {
                Assert.Equal(ownerThreadId, Thread.CurrentThread.ManagedThreadId);
                cancellationToken.ThrowIfCancellationRequested();
                return CompleteOnWorkerAsync(observation);
            }
        }

        private sealed class ThreadAffineOcrSink : IOcrObservationSink
        {
            private readonly int ownerThreadId;

            public ThreadAffineOcrSink(int ownerThreadId)
            {
                this.ownerThreadId = ownerThreadId;
            }

            public void Present(OcrObservation observation, ImageFrame frame)
            {
                Assert.Equal(ownerThreadId, Thread.CurrentThread.ManagedThreadId);
            }
        }

        private sealed class ThreadAffineTranslationEngine : ITranslationEngine
        {
            private readonly int ownerThreadId;

            public ThreadAffineTranslationEngine(int ownerThreadId)
            {
                this.ownerThreadId = ownerThreadId;
            }

            public int CallCount { get; private set; }

            public Task<string> TranslateAsync(
                string sourceText,
                string context,
                CancellationToken cancellationToken = default)
            {
                Assert.Equal(ownerThreadId, Thread.CurrentThread.ManagedThreadId);
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                return CompleteOnWorkerAsync("訳");
            }
        }
    }
}
