using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Audio;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class MoonshineGreedyAsrRuntimeTests
    {
        [Fact]
        public async Task GreedyRuntimeGeneratesUntilEosAndDecodesTranscript()
        {
            var backend = new ScriptedBackend(42, 99, MoonshineTinyAsrContract.EosTokenId);
            var decoder = new CapturingTokenDecoder("hello world");
            var runtime = new MoonshineGreedyAsrRuntime(backend, decoder);

            var observation = await runtime.TranscribePreparedAsync(
                new AudioChunk(new float[160], 16000, 5));

            Assert.True(observation.IsFinal);
            Assert.Equal("hello world", observation.Text);
            Assert.Equal(new[] { 42, 99 }, decoder.LastTokenIds);
            Assert.Equal(new[] { 1, 42, 99 }, backend.PreviousTokens);
            Assert.True(backend.SessionDisposed);
        }

        [Fact]
        public async Task OfflineEngineResamplesBeforeMoonshineGeneration()
        {
            var backend = new ScriptedBackend(MoonshineTinyAsrContract.EosTokenId);
            var runtime = new MoonshineGreedyAsrRuntime(backend, new CapturingTokenDecoder("done"));
            var engine = new OfflineAsrEngine(runtime);

            var observation = await engine.TranscribeAsync(
                new AudioChunk(new[] { 0f, 0.5f, 0f, -0.5f }, 8000, 77));

            Assert.Equal("done", observation.Text);
            Assert.NotNull(backend.StartedAudio);
            Assert.Equal(16000, backend.StartedAudio!.SampleRate);
            Assert.Equal(8, backend.StartedAudio.Samples.Length);
            Assert.Equal(77, backend.StartedAudio.TimestampMicroseconds);
        }

        [Fact]
        public async Task WrongPreparedSampleRateFailsBeforeBackendExecution()
        {
            var backend = new ScriptedBackend(MoonshineTinyAsrContract.EosTokenId);
            var runtime = new MoonshineGreedyAsrRuntime(backend, new CapturingTokenDecoder(""));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                runtime.TranscribePreparedAsync(new AudioChunk(new float[4], 48000, 0)));

            Assert.Null(backend.StartedAudio);
        }

        [Fact]
        public async Task VocabularyDriftFailsAndDisposesSession()
        {
            var backend = new WrongVocabularyBackend();
            var runtime = new MoonshineGreedyAsrRuntime(backend, new CapturingTokenDecoder(""));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.TranscribePreparedAsync(new AudioChunk(new float[4], 16000, 0)));

            Assert.Contains("vocabulary drift", error.Message);
            Assert.True(backend.SessionDisposed);
        }

        [Fact]
        public async Task NonFiniteLogitFailsInsteadOfChoosingArbitrarily()
        {
            var backend = new NonFiniteBackend();
            var runtime = new MoonshineGreedyAsrRuntime(backend, new CapturingTokenDecoder(""));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.TranscribePreparedAsync(new AudioChunk(new float[4], 16000, 0)));

            Assert.Contains("non-finite", error.Message);
            Assert.True(backend.SessionDisposed);
        }

        [Fact]
        public async Task GenerationLimitIsHardBoundAndTranscriptIsTrimmed()
        {
            var backend = new ScriptedBackend(10, 11, 12, 13);
            var decoder = new CapturingTokenDecoder("  partial transcript  ");
            var runtime = new MoonshineGreedyAsrRuntime(backend, decoder, maximumGenerationLength: 2);

            var observation = await runtime.TranscribePreparedAsync(
                new AudioChunk(new float[4], 16000, 0));

            Assert.Equal(new[] { 10, 11 }, decoder.LastTokenIds);
            Assert.Equal("partial transcript", observation.Text);
            Assert.Equal(2, backend.PreviousTokens.Count);
        }

        private sealed class CapturingTokenDecoder : IAsrTokenDecoder
        {
            private readonly string text;

            public CapturingTokenDecoder(string text)
            {
                this.text = text;
            }

            public IReadOnlyList<int> LastTokenIds { get; private set; } = Array.Empty<int>();

            public string Decode(IReadOnlyList<int> tokenIds)
            {
                LastTokenIds = tokenIds.ToArray();
                return text;
            }
        }

        private sealed class ScriptedBackend : IAudioSeq2SeqGenerationBackend
        {
            private readonly int[] tokens;

            public ScriptedBackend(params int[] tokens)
            {
                this.tokens = tokens;
            }

            public AudioChunk? StartedAudio { get; private set; }
            public List<int> PreviousTokens { get; } = new List<int>();
            public bool SessionDisposed { get; private set; }

            public Task<IAudioSeq2SeqGenerationSession> StartAsync(
                AudioChunk monoAudio,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                StartedAudio = monoAudio;
                return Task.FromResult<IAudioSeq2SeqGenerationSession>(new Session(this, tokens));
            }

            private sealed class Session : IAudioSeq2SeqGenerationSession
            {
                private readonly ScriptedBackend owner;
                private readonly int[] tokens;
                private int index;

                public Session(ScriptedBackend owner, int[] tokens)
                {
                    this.owner = owner;
                    this.tokens = tokens;
                }

                public Task<AsrDecoderStepResult> DecodeNextAsync(
                    int previousTokenId,
                    CancellationToken cancellationToken = default(CancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    owner.PreviousTokens.Add(previousTokenId);
                    var token = index < tokens.Length ? tokens[index++] : MoonshineTinyAsrContract.EosTokenId;
                    var logits = Enumerable.Repeat(-10f, MoonshineTinyAsrContract.VocabularySize).ToArray();
                    logits[token] = 10f;
                    return Task.FromResult(new AsrDecoderStepResult(logits));
                }

                public void Dispose()
                {
                    owner.SessionDisposed = true;
                }
            }
        }

        private sealed class WrongVocabularyBackend : IAudioSeq2SeqGenerationBackend
        {
            public bool SessionDisposed { get; private set; }

            public Task<IAudioSeq2SeqGenerationSession> StartAsync(
                AudioChunk monoAudio,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                return Task.FromResult<IAudioSeq2SeqGenerationSession>(new Session(this));
            }

            private sealed class Session : IAudioSeq2SeqGenerationSession
            {
                private readonly WrongVocabularyBackend owner;
                public Session(WrongVocabularyBackend owner) { this.owner = owner; }
                public Task<AsrDecoderStepResult> DecodeNextAsync(int previousTokenId, CancellationToken cancellationToken = default(CancellationToken))
                    => Task.FromResult(new AsrDecoderStepResult(new float[3]));
                public void Dispose() { owner.SessionDisposed = true; }
            }
        }

        private sealed class NonFiniteBackend : IAudioSeq2SeqGenerationBackend
        {
            public bool SessionDisposed { get; private set; }

            public Task<IAudioSeq2SeqGenerationSession> StartAsync(
                AudioChunk monoAudio,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                return Task.FromResult<IAudioSeq2SeqGenerationSession>(new Session(this));
            }

            private sealed class Session : IAudioSeq2SeqGenerationSession
            {
                private readonly NonFiniteBackend owner;
                public Session(NonFiniteBackend owner) { this.owner = owner; }
                public Task<AsrDecoderStepResult> DecodeNextAsync(int previousTokenId, CancellationToken cancellationToken = default(CancellationToken))
                {
                    var logits = new float[MoonshineTinyAsrContract.VocabularySize];
                    logits[17] = float.NaN;
                    return Task.FromResult(new AsrDecoderStepResult(logits));
                }
                public void Dispose() { owner.SessionDisposed = true; }
            }
        }
    }
}
