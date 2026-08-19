using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PhraseLayer.Core.Inputs
{
    public enum ImagePixelFormat
    {
        Unknown = 0,
        Gray8 = 1,
        Rgb24 = 2,
        Rgba32 = 3,
        Bgra32 = 4
    }

    public sealed class ImageFrame
    {
        public ImageFrame(byte[] pixels, int width, int height, long timestampMicroseconds)
            : this(pixels, width, height, timestampMicroseconds, ImagePixelFormat.Unknown) { }

        public ImageFrame(byte[] pixels, int width, int height, long timestampMicroseconds, ImagePixelFormat pixelFormat)
        {
            Pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            Width = width;
            Height = height;
            TimestampMicroseconds = timestampMicroseconds;
            PixelFormat = pixelFormat;
        }

        public byte[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }
        public long TimestampMicroseconds { get; }
        public ImagePixelFormat PixelFormat { get; }
    }

    public sealed class AudioChunk
    {
        public AudioChunk(float[] samples, int sampleRate, long timestampMicroseconds)
        {
            Samples = samples ?? throw new ArgumentNullException(nameof(samples));
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            SampleRate = sampleRate;
            TimestampMicroseconds = timestampMicroseconds;
        }

        public float[] Samples { get; }
        public int SampleRate { get; }
        public long TimestampMicroseconds { get; }
    }

    public sealed class OcrObservation
    {
        public OcrObservation(string text, double confidence)
            : this(text, confidence, Array.Empty<OcrRegion>()) { }

        public OcrObservation(string text, double confidence, IReadOnlyList<OcrRegion> regions)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            if (confidence < 0.0 || confidence > 1.0) throw new ArgumentOutOfRangeException(nameof(confidence));
            if (regions == null) throw new ArgumentNullException(nameof(regions));

            var snapshot = new OcrRegion[regions.Count];
            for (var index = 0; index < regions.Count; index++)
            {
                snapshot[index] = regions[index] ?? throw new ArgumentException("OCR regions cannot contain null entries.", nameof(regions));
            }

            Confidence = confidence;
            Regions = snapshot;
        }

        public string Text { get; }
        public double Confidence { get; }
        public IReadOnlyList<OcrRegion> Regions { get; }
    }

    public sealed class AsrObservation
    {
        public AsrObservation(string text, bool isFinal)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            IsFinal = isFinal;
        }

        public string Text { get; }
        public bool IsFinal { get; }
    }

    public interface IOcrEngine
    {
        Task<OcrObservation> RecognizeAsync(ImageFrame frame, CancellationToken cancellationToken = default(CancellationToken));
    }

    public interface IAsrEngine
    {
        Task<AsrObservation> TranscribeAsync(AudioChunk audio, CancellationToken cancellationToken = default(CancellationToken));
    }

    public sealed class FakeOcrEngine : IOcrEngine
    {
        private readonly OcrObservation observation;

        public FakeOcrEngine(string text, double confidence = 1.0)
            : this(new OcrObservation(text, confidence)) { }

        public FakeOcrEngine(OcrObservation observation)
        {
            this.observation = observation ?? throw new ArgumentNullException(nameof(observation));
        }

        public Task<OcrObservation> RecognizeAsync(ImageFrame frame, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(observation);
        }
    }

    public sealed class FakeAsrEngine : IAsrEngine
    {
        private readonly AsrObservation observation;

        public FakeAsrEngine(string text, bool isFinal = true)
        {
            observation = new AsrObservation(text, isFinal);
        }

        public Task<AsrObservation> TranscribeAsync(AudioChunk audio, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (audio == null) throw new ArgumentNullException(nameof(audio));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(observation);
        }
    }
}
