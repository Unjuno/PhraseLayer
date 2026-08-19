using System;
using System.Threading;
using System.Threading.Tasks;

namespace PhraseLayer.Core.Inputs
{
    public sealed class ImageFrame
    {
        public ImageFrame(byte[] pixels, int width, int height, long timestampMicroseconds)
        { Pixels = pixels ?? throw new ArgumentNullException(nameof(pixels)); Width = width; Height = height; TimestampMicroseconds = timestampMicroseconds; }
        public byte[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }
        public long TimestampMicroseconds { get; }
    }

    public sealed class AudioChunk
    {
        public AudioChunk(float[] samples, int sampleRate, long timestampMicroseconds)
        { Samples = samples ?? throw new ArgumentNullException(nameof(samples)); SampleRate = sampleRate; TimestampMicroseconds = timestampMicroseconds; }
        public float[] Samples { get; }
        public int SampleRate { get; }
        public long TimestampMicroseconds { get; }
    }

    public sealed class OcrObservation
    {
        public OcrObservation(string text, double confidence) { Text = text; Confidence = confidence; }
        public string Text { get; }
        public double Confidence { get; }
    }

    public sealed class AsrObservation
    {
        public AsrObservation(string text, bool isFinal) { Text = text; IsFinal = isFinal; }
        public string Text { get; }
        public bool IsFinal { get; }
    }

    public interface IOcrEngine { Task<OcrObservation> RecognizeAsync(ImageFrame frame, CancellationToken cancellationToken = default(CancellationToken)); }
    public interface IAsrEngine { Task<AsrObservation> TranscribeAsync(AudioChunk audio, CancellationToken cancellationToken = default(CancellationToken)); }

    public sealed class FakeOcrEngine : IOcrEngine
    {
        private readonly OcrObservation _observation;
        public FakeOcrEngine(string text, double confidence = 1.0) { _observation = new OcrObservation(text, confidence); }
        public Task<OcrObservation> RecognizeAsync(ImageFrame frame, CancellationToken cancellationToken = default(CancellationToken))
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_observation); }
    }

    public sealed class FakeAsrEngine : IAsrEngine
    {
        private readonly AsrObservation _observation;
        public FakeAsrEngine(string text, bool isFinal = true) { _observation = new AsrObservation(text, isFinal); }
        public Task<AsrObservation> TranscribeAsync(AudioChunk audio, CancellationToken cancellationToken = default(CancellationToken))
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_observation); }
    }
}
