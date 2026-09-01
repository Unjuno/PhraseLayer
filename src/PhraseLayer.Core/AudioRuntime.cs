using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Inputs;

namespace PhraseLayer.Core.Audio
{
    /// <summary>
    /// Pure Core audio preparation used before an offline ASR backend. It deliberately owns only
    /// deterministic sample validation/clamping and sample-rate conversion; microphone capture,
    /// VAD, denoising, and model-specific feature extraction remain replaceable platform/runtime concerns.
    /// </summary>
    public static class AudioChunkPreprocessor
    {
        private const int DownsampleFilterHalfWidth = 16;
        private const double DownsampleNyquistGuard = 0.94;

        public static AudioChunk PrepareMono(AudioChunk input, int targetSampleRate)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (targetSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetSampleRate));

            var source = ValidateAndClamp(input.Samples);
            if (source.Length == 0 || input.SampleRate == targetSampleRate)
                return new AudioChunk(source, targetSampleRate, input.TimestampMicroseconds);

            var output = targetSampleRate > input.SampleRate
                ? ResampleLinear(source, input.SampleRate, targetSampleRate)
                : ResampleBandLimited(source, input.SampleRate, targetSampleRate);
            return new AudioChunk(output, targetSampleRate, input.TimestampMicroseconds);
        }

        private static float[] ResampleLinear(float[] source, int sourceSampleRate, int targetSampleRate)
        {
            var outputLength = GetOutputLength(source.Length, sourceSampleRate, targetSampleRate);
            var output = new float[outputLength];
            if (source.Length == 1)
            {
                for (var index = 0; index < output.Length; index++) output[index] = source[0];
                return output;
            }

            var ratio = sourceSampleRate / (double)targetSampleRate;
            for (var index = 0; index < output.Length; index++)
            {
                var sourcePosition = index * ratio;
                if (sourcePosition >= source.Length - 1)
                {
                    output[index] = source[source.Length - 1];
                    continue;
                }

                var lower = (int)Math.Floor(sourcePosition);
                var upper = lower + 1;
                var blend = sourcePosition - lower;
                output[index] = (float)(source[lower] + ((source[upper] - source[lower]) * blend));
            }
            return output;
        }

        private static float[] ResampleBandLimited(float[] source, int sourceSampleRate, int targetSampleRate)
        {
            var outputLength = GetOutputLength(source.Length, sourceSampleRate, targetSampleRate);
            var output = new float[outputLength];
            if (source.Length == 1)
            {
                for (var index = 0; index < output.Length; index++) output[index] = source[0];
                return output;
            }

            var sourcePerOutput = sourceSampleRate / (double)targetSampleRate;
            var cutoff = (targetSampleRate / (double)sourceSampleRate) * DownsampleNyquistGuard;
            for (var outputIndex = 0; outputIndex < output.Length; outputIndex++)
            {
                var sourcePosition = outputIndex * sourcePerOutput;
                var firstSourceIndex = (int)Math.Ceiling(sourcePosition - DownsampleFilterHalfWidth);
                var lastSourceIndex = (int)Math.Floor(sourcePosition + DownsampleFilterHalfWidth);
                double weightedSum = 0.0;
                double weightSum = 0.0;

                for (var sourceIndex = firstSourceIndex; sourceIndex <= lastSourceIndex; sourceIndex++)
                {
                    var distance = sourcePosition - sourceIndex;
                    var normalizedDistance = distance / DownsampleFilterHalfWidth;
                    if (normalizedDistance < -1.0 || normalizedDistance > 1.0)
                        continue;

                    // Hann-windowed sinc low-pass. The cutoff is reduced below the target Nyquist frequency
                    // so source content that cannot be represented after decimation is attenuated before sampling.
                    var window = 0.5 * (1.0 + Math.Cos(Math.PI * normalizedDistance));
                    var weight = cutoff * NormalizedSinc(distance * cutoff) * window;
                    var clampedSourceIndex = sourceIndex;
                    if (clampedSourceIndex < 0) clampedSourceIndex = 0;
                    if (clampedSourceIndex >= source.Length) clampedSourceIndex = source.Length - 1;

                    weightedSum += source[clampedSourceIndex] * weight;
                    weightSum += weight;
                }

                double value;
                if (Math.Abs(weightSum) > 1e-12)
                {
                    value = weightedSum / weightSum;
                }
                else
                {
                    var nearest = (int)Math.Round(sourcePosition, MidpointRounding.AwayFromZero);
                    if (nearest < 0) nearest = 0;
                    if (nearest >= source.Length) nearest = source.Length - 1;
                    value = source[nearest];
                }

                if (value > 1.0) value = 1.0;
                if (value < -1.0) value = -1.0;
                output[outputIndex] = (float)value;
            }

            return output;
        }

        private static int GetOutputLength(int sourceLength, int sourceSampleRate, int targetSampleRate)
        {
            return Math.Max(
                1,
                (int)Math.Round(
                    sourceLength * (double)targetSampleRate / sourceSampleRate,
                    MidpointRounding.AwayFromZero));
        }

        private static double NormalizedSinc(double value)
        {
            if (Math.Abs(value) < 1e-12)
                return 1.0;
            var angle = Math.PI * value;
            return Math.Sin(angle) / angle;
        }

        private static float[] ValidateAndClamp(IReadOnlyList<float> samples)
        {
            var output = new float[samples.Count];
            for (var index = 0; index < samples.Count; index++)
            {
                var sample = samples[index];
                if (float.IsNaN(sample) || float.IsInfinity(sample))
                    throw new ArgumentException("Audio samples must be finite.", nameof(samples));
                if (sample > 1f) sample = 1f;
                if (sample < -1f) sample = -1f;
                output[index] = sample;
            }
            return output;
        }
    }

    /// <summary>
    /// Minimal WAV decoder for repeatable ASR fixtures. Supports little-endian RIFF/WAVE PCM16 and
    /// IEEE float32, any positive channel count, and downmixes channels to mono by arithmetic mean.
    /// It intentionally does not perform sample-rate conversion; AudioChunkPreprocessor owns that step.
    /// </summary>
    public static class WaveAudioDecoder
    {
        public static AudioChunk Decode(byte[] wavBytes, long timestampMicroseconds = 0)
        {
            if (wavBytes == null) throw new ArgumentNullException(nameof(wavBytes));
            if (timestampMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(timestampMicroseconds));
            if (wavBytes.Length < 12 ||
                ReadAscii(wavBytes, 0, 4) != "RIFF" ||
                ReadAscii(wavBytes, 8, 4) != "WAVE")
                throw new ArgumentException("Input is not a RIFF/WAVE file.", nameof(wavBytes));

            ushort format = 0;
            ushort channels = 0;
            var sampleRate = 0;
            ushort blockAlign = 0;
            ushort bitsPerSample = 0;
            var dataOffset = -1;
            var dataLength = 0;

            var offset = 12;
            while (offset <= wavBytes.Length - 8)
            {
                var id = ReadAscii(wavBytes, offset, 4);
                var chunkLength = checked((int)ReadUInt32LittleEndian(wavBytes, offset + 4));
                var chunkOffset = offset + 8;
                if (chunkLength < 0 || chunkOffset > wavBytes.Length - chunkLength)
                    throw new ArgumentException("WAV chunk length exceeds the input buffer.", nameof(wavBytes));

                if (id == "fmt ")
                {
                    if (chunkLength < 16)
                        throw new ArgumentException("WAV fmt chunk is too short.", nameof(wavBytes));
                    format = ReadUInt16LittleEndian(wavBytes, chunkOffset);
                    channels = ReadUInt16LittleEndian(wavBytes, chunkOffset + 2);
                    sampleRate = checked((int)ReadUInt32LittleEndian(wavBytes, chunkOffset + 4));
                    blockAlign = ReadUInt16LittleEndian(wavBytes, chunkOffset + 12);
                    bitsPerSample = ReadUInt16LittleEndian(wavBytes, chunkOffset + 14);
                }
                else if (id == "data" && dataOffset < 0)
                {
                    dataOffset = chunkOffset;
                    dataLength = chunkLength;
                }

                offset = chunkOffset + chunkLength + (chunkLength & 1);
            }

            if (channels == 0 || sampleRate <= 0 || blockAlign == 0 || dataOffset < 0)
                throw new ArgumentException("WAV file is missing a supported fmt or data chunk.", nameof(wavBytes));

            var bytesPerSample = bitsPerSample / 8;
            if ((format != 1 || bitsPerSample != 16) && (format != 3 || bitsPerSample != 32))
                throw new NotSupportedException("Only PCM16 and IEEE float32 WAV fixtures are supported.");
            if (bytesPerSample == 0 || blockAlign != channels * bytesPerSample)
                throw new ArgumentException("WAV block alignment does not match channel/sample width.", nameof(wavBytes));
            if (dataLength % blockAlign != 0)
                throw new ArgumentException("WAV data chunk does not contain whole sample frames.", nameof(wavBytes));

            var frameCount = dataLength / blockAlign;
            var mono = new float[frameCount];
            var cursor = dataOffset;
            for (var frame = 0; frame < frameCount; frame++)
            {
                double sum = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    if (format == 1)
                    {
                        var value = unchecked((short)ReadUInt16LittleEndian(wavBytes, cursor));
                        sum += value / 32768.0;
                    }
                    else
                    {
                        var bits = unchecked((int)ReadUInt32LittleEndian(wavBytes, cursor));
                        var value = BitConverter.Int32BitsToSingle(bits);
                        if (float.IsNaN(value) || float.IsInfinity(value))
                            throw new ArgumentException("WAV contains a non-finite float sample.", nameof(wavBytes));
                        sum += value;
                    }
                    cursor += bytesPerSample;
                }
                var mixed = (float)(sum / channels);
                if (mixed > 1f) mixed = 1f;
                if (mixed < -1f) mixed = -1f;
                mono[frame] = mixed;
            }

            return new AudioChunk(mono, sampleRate, timestampMicroseconds);
        }

        private static string ReadAscii(byte[] data, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset));
            var chars = new char[count];
            for (var index = 0; index < count; index++) chars[index] = (char)data[offset + index];
            return new string(chars);
        }

        private static ushort ReadUInt16LittleEndian(byte[] data, int offset)
        {
            if (offset < 0 || offset > data.Length - 2) throw new ArgumentOutOfRangeException(nameof(offset));
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        private static uint ReadUInt32LittleEndian(byte[] data, int offset)
        {
            if (offset < 0 || offset > data.Length - 4) throw new ArgumentOutOfRangeException(nameof(offset));
            return (uint)(data[offset] |
                          (data[offset + 1] << 8) |
                          (data[offset + 2] << 16) |
                          (data[offset + 3] << 24));
        }
    }

    public interface IOfflineAsrRuntime
    {
        int RequiredSampleRate { get; }
        Task<AsrObservation> TranscribePreparedAsync(
            AudioChunk monoAudio,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>
    /// Core adapter that normalizes any mono AudioChunk to a model runtime's required sample rate.
    /// Concrete Moonshine/Whisper/other inference implementations live outside Core.
    /// </summary>
    public sealed class OfflineAsrEngine : IAsrEngine
    {
        private readonly IOfflineAsrRuntime runtime;

        public OfflineAsrEngine(IOfflineAsrRuntime runtime)
        {
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            if (runtime.RequiredSampleRate <= 0)
                throw new ArgumentException("ASR runtime sample rate must be positive.", nameof(runtime));
        }

        public async Task<AsrObservation> TranscribeAsync(
            AudioChunk audio,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (audio == null) throw new ArgumentNullException(nameof(audio));
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = AudioChunkPreprocessor.PrepareMono(audio, runtime.RequiredSampleRate);
            return await runtime.TranscribePreparedAsync(prepared, cancellationToken);
        }
    }
}
