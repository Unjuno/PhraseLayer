using System;

namespace PhraseLayer.Core.Inputs
{
    /// <summary>
    /// Receives OCR observations together with the exact source frame whose pixel geometry they reference.
    /// </summary>
    public interface IOcrObservationSink
    {
        void Present(OcrObservation observation, ImageFrame frame);
    }

    /// <summary>
    /// Bridges scheduler results to a presentation sink while preventing a processed observation from being
    /// paired with the wrong camera frame. Skipped scheduler results leave the last presentation untouched.
    /// </summary>
    public sealed class OcrPresentationCoordinator
    {
        private readonly IOcrObservationSink sink;

        public OcrPresentationCoordinator(IOcrObservationSink sink)
        {
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        public bool PresentIfProcessed(OcrScheduleResult result, ImageFrame frame)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            if (!result.WasProcessed) return false;
            if (result.Observation == null)
                throw new InvalidOperationException("A processed OCR schedule result must contain an observation.");
            if (result.FrameTimestampMicroseconds != frame.TimestampMicroseconds)
                throw new InvalidOperationException(
                    "OCR presentation frame mismatch. The observation must be presented against the exact frame used for inference.");

            sink.Present(result.Observation, frame);
            return true;
        }
    }
}
