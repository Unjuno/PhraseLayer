using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class OcrPresentationTests
    {
        [Fact]
        public void ProcessedResultPresentsObservationWithExactFrame()
        {
            var sink = new RecordingSink();
            var coordinator = new OcrPresentationCoordinator(sink);
            var observation = new OcrObservation("exit", 0.9, Array.Empty<OcrRegion>());
            var frame = new ImageFrame(new byte[4], 10, 20, 1234);
            var result = new OcrScheduleResult(OcrScheduleStatus.Processed, 1234, observation);

            var presented = coordinator.PresentIfProcessed(result, frame);

            Assert.True(presented);
            Assert.Same(observation, sink.Observation);
            Assert.Same(frame, sink.Frame);
        }

        [Fact]
        public void SkippedResultDoesNotReplaceExistingPresentation()
        {
            var sink = new RecordingSink();
            var coordinator = new OcrPresentationCoordinator(sink);
            var frame = new ImageFrame(new byte[4], 10, 20, 1234);
            var result = new OcrScheduleResult(OcrScheduleStatus.SkippedBusy, 1234, null);

            var presented = coordinator.PresentIfProcessed(result, frame);

            Assert.False(presented);
            Assert.Null(sink.Observation);
            Assert.Null(sink.Frame);
        }

        [Fact]
        public void ProcessedResultRejectsMismatchedFrameTimestamp()
        {
            var sink = new RecordingSink();
            var coordinator = new OcrPresentationCoordinator(sink);
            var observation = new OcrObservation("exit", 0.9, Array.Empty<OcrRegion>());
            var result = new OcrScheduleResult(OcrScheduleStatus.Processed, 1234, observation);
            var wrongFrame = new ImageFrame(new byte[4], 10, 20, 9999);

            Assert.Throws<InvalidOperationException>(() => coordinator.PresentIfProcessed(result, wrongFrame));
            Assert.Null(sink.Observation);
        }

        [Fact]
        public void ProcessedResultRequiresObservation()
        {
            var sink = new RecordingSink();
            var coordinator = new OcrPresentationCoordinator(sink);
            var frame = new ImageFrame(new byte[4], 10, 20, 1234);
            var result = new OcrScheduleResult(OcrScheduleStatus.Processed, 1234, null);

            Assert.Throws<InvalidOperationException>(() => coordinator.PresentIfProcessed(result, frame));
        }

        private sealed class RecordingSink : IOcrObservationSink
        {
            public OcrObservation? Observation { get; private set; }
            public ImageFrame? Frame { get; private set; }

            public void Present(OcrObservation observation, ImageFrame frame)
            {
                Observation = observation;
                Frame = frame;
            }
        }
    }
}
