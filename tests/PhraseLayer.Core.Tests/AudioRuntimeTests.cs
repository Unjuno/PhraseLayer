using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Audio;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class AudioRuntimeTests
    {
        [Fact]
        public void PrepareMonoClampsAndPreservesTimestampAtNativeRate()
        {
            var prepared = AudioChunkPreprocessor.PrepareMono(
                new AudioChunk(new[] { -2f, -0.25f, 0.5f, 2f }, 16000, 123),
                16000);

            Assert.Equal(16000, prepared.SampleRate);
            Assert.Equal(123, prepared.TimestampMicroseconds);
            Assert.Equal(new[] { -1f, -0.25f, 0.5f, 1f }, prepared.Samples);
        }

        [Fact]
        public void PrepareMonoResamplesToRequestedRate()
        {
            var prepared = AudioChunkPreprocessor.PrepareMono(
                new AudioChunk(new[] { 0f, 0.5f, 1f, 0.5f }, 8000, 7),
                16000);

            Assert.Equal(16000, prepared.SampleRate);
            Assert.Equal(8, prepared.Samples.Length);
            Assert.Equal(0f, prepared.Samples[0], 4);
            Assert.Equal(0.25f, prepared.Samples[1], 4);
            Assert.Equal(0.5f, prepared.Samples[2], 4);
            Assert.Equal(1f, prepared.Samples[4], 4);
        }

        [Fact]
        public void PrepareMonoDownsamplingAttenuatesContentAboveTargetNyquist()
        {
            const int sourceRate = 48000;
            const int targetRate = 16000;
            const int sourceFrequency = 12000;
            var source = new float[sourceRate / 10];
            for (var index = 0; index < source.Length; index++)
            {
                source[index] = (float)Math.Sin(
                    2.0 * Math.PI * sourceFrequency * index / sourceRate);
            }

            var prepared = AudioChunkPreprocessor.PrepareMono(
                new AudioChunk(source, sourceRate, 17),
                targetRate);

            double squared = 0.0;
            for (var index = 0; index < prepared.Samples.Length; index++)
                squared += prepared.Samples[index] * (double)prepared.Samples[index];
            var rms = Math.Sqrt(squared / prepared.Samples.Length);

            Assert.Equal(targetRate, prepared.SampleRate);
            Assert.Equal(source.Length / 3, prepared.Samples.Length);
            Assert.True(rms < 0.05, $"Aliased 12 kHz energy was not sufficiently attenuated; RMS={rms:F6}.");
        }

        [Fact]
        public void PrepareMonoDownsamplingPreservesDcGain()
        {
            var source = new float[4800];
            Array.Fill(source, 0.25f);

            var prepared = AudioChunkPreprocessor.PrepareMono(
                new AudioChunk(source, 48000, 18),
                16000);

            Assert.Equal(1600, prepared.Samples.Length);
            foreach (var sample in prepared.Samples)
                Assert.Equal(0.25f, sample, 4);
        }

        [Fact]
        public void PrepareMonoRejectsNonFiniteSamples()
        {
            var error = Assert.Throws<ArgumentException>(() =>
                AudioChunkPreprocessor.PrepareMono(
                    new AudioChunk(new[] { 0f, float.NaN }, 16000, 0),
                    16000));

            Assert.Contains("finite", error.Message);
        }

        [Fact]
        public void WaveDecoderReadsPcm16Mono()
        {
            var wav = BuildPcm16Wave(
                sampleRate: 8000,
                channels: 1,
                interleavedSamples: new short[] { short.MinValue, 0, short.MaxValue });

            var audio = WaveAudioDecoder.Decode(wav, 99);

            Assert.Equal(8000, audio.SampleRate);
            Assert.Equal(99, audio.TimestampMicroseconds);
            Assert.Equal(3, audio.Samples.Length);
            Assert.Equal(-1f, audio.Samples[0], 4);
            Assert.Equal(0f, audio.Samples[1], 4);
            Assert.InRange(audio.Samples[2], 0.9998f, 1f);
        }

        [Fact]
        public void WaveDecoderDownmixesStereoToMono()
        {
            var wav = BuildPcm16Wave(
                sampleRate: 16000,
                channels: 2,
                interleavedSamples: new short[]
                {
                    short.MaxValue, short.MinValue,
                    16384, 16384
                });

            var audio = WaveAudioDecoder.Decode(wav);

            Assert.Equal(2, audio.Samples.Length);
            Assert.InRange(audio.Samples[0], -0.0001f, 0.0001f);
            Assert.Equal(0.5f, audio.Samples[1], 4);
        }

        [Fact]
        public async Task OfflineAsrEngineAlwaysFeedsRuntimeRequiredSampleRate()
        {
            var runtime = new CapturingRuntime(16000, "hello world");
            var engine = new OfflineAsrEngine(runtime);

            var observation = await engine.TranscribeAsync(
                new AudioChunk(new[] { 0f, 0.5f, 0f, -0.5f }, 8000, 42));

            Assert.Equal("hello world", observation.Text);
            Assert.NotNull(runtime.LastAudio);
            Assert.Equal(16000, runtime.LastAudio!.SampleRate);
            Assert.Equal(8, runtime.LastAudio.Samples.Length);
            Assert.Equal(42, runtime.LastAudio.TimestampMicroseconds);
        }

        private static byte[] BuildPcm16Wave(int sampleRate, short channels, short[] interleavedSamples)
        {
            var dataLength = interleavedSamples.Length * 2;
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            writer.Write(36 + dataLength);
            writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
            writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * 2);
            writer.Write((short)(channels * 2));
            writer.Write((short)16);
            writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            writer.Write(dataLength);
            foreach (var sample in interleavedSamples) writer.Write(sample);
            writer.Flush();
            return stream.ToArray();
        }

        private sealed class CapturingRuntime : IOfflineAsrRuntime
        {
            private readonly string text;

            public CapturingRuntime(int requiredSampleRate, string text)
            {
                RequiredSampleRate = requiredSampleRate;
                this.text = text;
            }

            public int RequiredSampleRate { get; }
            public AudioChunk? LastAudio { get; private set; }

            public Task<AsrObservation> TranscribePreparedAsync(
                AudioChunk monoAudio,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastAudio = monoAudio;
                return Task.FromResult(new AsrObservation(text, true));
            }
        }
    }
}
