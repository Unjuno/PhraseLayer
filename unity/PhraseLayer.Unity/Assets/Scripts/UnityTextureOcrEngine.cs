using System;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Inputs;
using UnityEngine;

namespace PhraseLayer.Unity
{
    public interface IUnityTextureOcrBackend
    {
        Task<OcrObservation> RecognizeAsync(
            Texture texture,
            int sourceWidth,
            int sourceHeight,
            long timestampMicroseconds,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>
    /// Adapts a Unity Texture OCR backend to the platform-neutral IOcrEngine contract.
    /// The camera texture remains native unless a concrete backend explicitly chooses CPU readback.
    /// </summary>
    public sealed class UnityTextureOcrEngine : IOcrEngine
    {
        private readonly IUnityTextureOcrBackend backend;

        public UnityTextureOcrEngine(IUnityTextureOcrBackend backend)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public Task<OcrObservation> RecognizeAsync(
            ImageFrame frame,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            cancellationToken.ThrowIfCancellationRequested();

            var payload = frame.NativePayload as UnityTextureFramePayload;
            if (payload == null)
            {
                throw new InvalidOperationException(
                    "UnityTextureOcrEngine requires ImageFrame.NativePayload to be a UnityTextureFramePayload. " +
                    "Use a separate CPU OCR adapter for byte-backed frames.");
            }

            return backend.RecognizeAsync(
                payload.Texture,
                frame.Width,
                frame.Height,
                frame.TimestampMicroseconds,
                cancellationToken);
        }
    }

    /// <summary>
    /// Deterministic Editor/testing backend. Real PP-OCR or other model backends must implement the same interface.
    /// </summary>
    public sealed class FixedUnityTextureOcrBackend : IUnityTextureOcrBackend
    {
        private readonly OcrObservation observation;

        public FixedUnityTextureOcrBackend(OcrObservation observation)
        {
            this.observation = observation ?? throw new ArgumentNullException(nameof(observation));
        }

        public Task<OcrObservation> RecognizeAsync(
            Texture texture,
            int sourceWidth,
            int sourceHeight,
            long timestampMicroseconds,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
            if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(observation);
        }
    }
}
