using System;
using System.IO;
using System.Text;
using PhraseLayer.Core.Audio;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class MoonshineBinaryTokenDecoderTests
    {
        [Fact]
        public void DecodesSpaceMarkerSkipsSpecialsAndFusesByteFallback()
        {
            var entries = EmptyReviewedVocabulary();
            entries[3] = Encoding.UTF8.GetBytes("▁caf");
            entries[4] = new byte[] { 0xC3 };
            entries[5] = new byte[] { 0xA9 };
            entries[6] = Encoding.UTF8.GetBytes("▁today");
            entries[32000] = Encoding.UTF8.GetBytes("<<ST_0>>");
            var decoder = new MoonshineBinaryTokenDecoder(BuildAsset(entries));

            var text = decoder.Decode(new[] { 1, 3, 4, 5, 32000, 6, 2 });

            Assert.Equal("café today", text);
            Assert.Equal(MoonshineTinyAsrContract.VocabularySize, decoder.TokenCount);
        }

        [Fact]
        public void RejectsInvalidTokenIdAndInvalidUtf8()
        {
            var entries = EmptyReviewedVocabulary();
            entries[3] = Encoding.UTF8.GetBytes("▁ok");
            entries[4] = new byte[] { 0xC3 };
            var decoder = new MoonshineBinaryTokenDecoder(BuildAsset(entries));

            Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Decode(new[] { 32768 }));
            Assert.Throws<InvalidDataException>(() => decoder.Decode(new[] { 4 }));
        }

        [Fact]
        public void RejectsTruncatedOrWrongSizedAssets()
        {
            Assert.Throws<InvalidDataException>(() => new MoonshineBinaryTokenDecoder(new byte[] { 5, (byte)'a' }));

            var tooSmall = new[]
            {
                Encoding.UTF8.GetBytes("<unk>"),
                Encoding.UTF8.GetBytes("<s>"),
                Encoding.UTF8.GetBytes("</s>")
            };
            Assert.Throws<InvalidDataException>(() => new MoonshineBinaryTokenDecoder(BuildAsset(tooSmall)));
        }

        [Fact]
        public void GreedyRuntimeCanUseManagedBinaryDecoderEndToEnd()
        {
            var entries = EmptyReviewedVocabulary();
            entries[3] = Encoding.UTF8.GetBytes("▁hello");
            entries[4] = Encoding.UTF8.GetBytes("▁world");
            var decoder = new MoonshineBinaryTokenDecoder(BuildAsset(entries));
            var runtime = new MoonshineGreedyAsrRuntime(
                new ScriptedBackend(3, 4, MoonshineTinyAsrContract.EosTokenId),
                decoder,
                maximumGenerationLength: 8);

            var observation = runtime.TranscribePreparedAsync(
                new PhraseLayer.Core.Inputs.AudioChunk(new float[160], 16000, 1)).GetAwaiter().GetResult();

            Assert.True(observation.IsFinal);
            Assert.Equal("hello world", observation.Text);
        }

        private static byte[][] EmptyReviewedVocabulary()
        {
            var result = new byte[MoonshineTinyAsrContract.VocabularySize][];
            for (var index = 0; index < result.Length; index++)
                result[index] = Encoding.UTF8.GetBytes("<x" + index + ">");
            result[0] = Encoding.UTF8.GetBytes("<unk>");
            result[1] = Encoding.UTF8.GetBytes("<s>");
            result[2] = Encoding.UTF8.GetBytes("</s>");
            return result;
        }

        private static byte[] BuildAsset(byte[][] entries)
        {
            using (var stream = new MemoryStream())
            {
                foreach (var entry in entries)
                {
                    if (entry.Length == 0)
                    {
                        stream.WriteByte(0);
                    }
                    else if (entry.Length < 128)
                    {
                        stream.WriteByte((byte)entry.Length);
                    }
                    else
                    {
                        stream.WriteByte((byte)(128 + entry.Length % 128));
                        stream.WriteByte((byte)(entry.Length / 128));
                    }
                    stream.Write(entry, 0, entry.Length);
                }
                return stream.ToArray();
            }
        }

        private sealed class ScriptedBackend : IAudioSeq2SeqGenerationBackend
        {
            private readonly int[] tokens;
            public ScriptedBackend(params int[] tokens) { this.tokens = tokens; }
            public System.Threading.Tasks.Task<IAudioSeq2SeqGenerationSession> StartAsync(
                PhraseLayer.Core.Inputs.AudioChunk monoAudio,
                System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
            {
                return System.Threading.Tasks.Task.FromResult<IAudioSeq2SeqGenerationSession>(new Session(tokens));
            }

            private sealed class Session : IAudioSeq2SeqGenerationSession
            {
                private readonly int[] tokens;
                private int index;
                public Session(int[] tokens) { this.tokens = tokens; }
                public System.Threading.Tasks.Task<AsrDecoderStepResult> DecodeNextAsync(
                    int previousTokenId,
                    System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var logits = new float[MoonshineTinyAsrContract.VocabularySize];
                    var token = tokens[index++];
                    logits[token] = 1f;
                    return System.Threading.Tasks.Task.FromResult(new AsrDecoderStepResult(logits));
                }
                public void Dispose() { }
            }
        }
    }
}
