using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class ParityVerifiedTranslationTokenizerTests
    {
        [Fact]
        public void VerifyCreatesDelegatingMarkerOnlyAfterExactParity()
        {
            var tokenizer = new RecordingTokenizer(new[] { 7, 0 }, "訳");
            var verified = ParityVerifiedTranslationTokenizer.Verify(tokenizer, Fixtures());

            Assert.Contains("parity=exact", verified.ParityReport);
            Assert.Equal(new[] { 7, 0 }, verified.Encode("source"));
            Assert.Equal("訳", verified.Decode(new[] { 8 }));
        }

        [Fact]
        public void OfficialFactoryRejectsTokenizerBeforeBackendCanRun()
        {
            var tokenizer = new RecordingTokenizer(new[] { 99, 0 }, "訳");
            var backend = new CountingBackend();

            Assert.Throws<InvalidOperationException>(() =>
                OpusMtEnJapLocalEngineFactory.CreateReferenceEngine(tokenizer, Fixtures(), backend));
            Assert.Equal(0, backend.Calls);
        }

        [Fact]
        public async Task OfficialFactoryAddsBoundedTranslationCache()
        {
            var tokenizer = new RecordingTokenizer(new[] { 7, 0 }, "訳");
            var backend = new CountingBackend();
            var engine = OpusMtEnJapLocalEngineFactory.CreateReferenceEngine(
                tokenizer,
                Fixtures(),
                backend,
                cacheEntries: 4);

            var first = await engine.TranslateAsync("source", "context");
            var callsAfterFirst = backend.Calls;
            var second = await engine.TranslateAsync("source", "context");

            Assert.Equal("訳", first);
            Assert.Equal("訳", second);
            Assert.True(callsAfterFirst > 0);
            Assert.Equal(callsAfterFirst, backend.Calls);
        }

        private static TranslationTokenizerFixtureSet Fixtures()
        {
            return new TranslationTokenizerFixtureSet(
                new[] { new TranslationTokenizerEncodeFixture("source", new[] { 7, 0 }) },
                new[] { new TranslationTokenizerDecodeFixture(new[] { 8 }, "訳") });
        }

        private sealed class RecordingTokenizer : ITranslationTokenizer
        {
            private readonly IReadOnlyList<int> encoded;
            private readonly string decoded;

            public RecordingTokenizer(IReadOnlyList<int> encoded, string decoded)
            {
                this.encoded = encoded;
                this.decoded = decoded;
            }

            public IReadOnlyList<int> Encode(string text)
            {
                return encoded;
            }

            public string Decode(IReadOnlyList<int> tokenIds)
            {
                return decoded;
            }
        }

        private sealed class CountingBackend : IAutoregressiveTranslationBackend
        {
            public int Calls { get; private set; }

            public Task<IReadOnlyList<TranslationTokenCandidate>> PredictNextAsync(
                IReadOnlyList<int> sourceTokenIds,
                IReadOnlyList<int> generatedTokenIds,
                int maxCandidates,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                Calls++;
                IReadOnlyList<TranslationTokenCandidate> result = generatedTokenIds.Count == 1
                    ? new[] { new TranslationTokenCandidate(8, 0.0) }
                    : new[] { new TranslationTokenCandidate(OpusMtEnJapGenerationContract.EosTokenId, 0.0) };
                return Task.FromResult(result);
            }
        }
    }
}
