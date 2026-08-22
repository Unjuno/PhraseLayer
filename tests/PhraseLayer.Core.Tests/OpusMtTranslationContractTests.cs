using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class OpusMtTranslationContractTests
    {
        [Fact]
        public void ReferenceOptionsMatchPinnedGenerationConfig()
        {
            var options = OpusMtEnJapGenerationContract.CreateReferenceOptions();

            Assert.Equal(46275, options.DecoderStartTokenId);
            Assert.Equal(0, options.EosTokenId);
            Assert.Equal(46275, options.PadTokenId);
            Assert.Equal(512, options.MaxLength);
            Assert.Equal(4, options.BeamWidth);
            Assert.Equal(1.0, options.LengthPenalty);
            Assert.Equal(0, OpusMtEnJapGenerationContract.BosTokenId);
            Assert.Equal(0, OpusMtEnJapGenerationContract.ForcedEosTokenId);
        }

        [Fact]
        public async Task ForcedEosReplacesBackendAtFinalDecoderSlot()
        {
            var inner = new RecordingBackend();
            var backend = new ForcedEosTranslationBackend(inner, forcedEosTokenId: 0, maxLength: 4);

            var candidates = await backend.PredictNextAsync(
                new[] { 100, 101 },
                new[] { 46275, 20, 21 },
                maxCandidates: 4);

            Assert.False(inner.Called);
            var candidate = Assert.Single(candidates);
            Assert.Equal(0, candidate.TokenId);
            Assert.Equal(0.0, candidate.LogProbability);
        }

        [Fact]
        public async Task ForcedEosDelegatesBeforeFinalDecoderSlot()
        {
            var inner = new RecordingBackend();
            var backend = new ForcedEosTranslationBackend(inner, forcedEosTokenId: 0, maxLength: 4);

            var candidates = await backend.PredictNextAsync(
                new[] { 100, 101 },
                new[] { 46275, 20 },
                maxCandidates: 4);

            Assert.True(inner.Called);
            var candidate = Assert.Single(candidates);
            Assert.Equal(77, candidate.TokenId);
        }

        [Fact]
        public async Task ReferenceEngineEndsWithForcedEosAtMaxLength()
        {
            // Use the small decorator directly so the test is cheap while exercising the same engine boundary.
            var tokenizer = new EchoTokenizer();
            var inner = new RecordingBackend(alwaysToken: 77);
            var backend = new ForcedEosTranslationBackend(inner, forcedEosTokenId: 0, maxLength: 4);
            var engine = new AutoregressiveTranslationEngine(
                tokenizer,
                backend,
                new TranslationGenerationOptions(
                    decoderStartTokenId: 46275,
                    eosTokenId: 0,
                    padTokenId: 46275,
                    maxLength: 4,
                    beamWidth: 1));

            var translated = await engine.TranslateAsync("hello", "hello");

            Assert.Equal("77,77", translated);
            Assert.Equal(2, inner.CallCount);
        }

        private sealed class RecordingBackend : IAutoregressiveTranslationBackend
        {
            private readonly int alwaysToken;

            public RecordingBackend(int alwaysToken = 77)
            {
                this.alwaysToken = alwaysToken;
            }

            public bool Called => CallCount > 0;
            public int CallCount { get; private set; }

            public Task<IReadOnlyList<TranslationTokenCandidate>> PredictNextAsync(
                IReadOnlyList<int> sourceTokenIds,
                IReadOnlyList<int> generatedTokenIds,
                int maxCandidates,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                IReadOnlyList<TranslationTokenCandidate> result = new[]
                {
                    new TranslationTokenCandidate(alwaysToken, -0.1)
                };
                return Task.FromResult(result);
            }
        }

        private sealed class EchoTokenizer : ITranslationTokenizer
        {
            public IReadOnlyList<int> Encode(string text)
            {
                if (text == null) throw new ArgumentNullException(nameof(text));
                return new[] { 1 };
            }

            public string Decode(IReadOnlyList<int> tokenIds)
            {
                if (tokenIds == null) throw new ArgumentNullException(nameof(tokenIds));
                return string.Join(",", tokenIds);
            }
        }
    }
}
