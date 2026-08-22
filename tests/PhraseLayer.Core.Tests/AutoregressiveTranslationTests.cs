using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class AutoregressiveTranslationTests
    {
        private const int Start = 46275;
        private const int Eos = 0;
        private const int Pad = 46275;

        [Fact]
        public async Task GreedyReferenceDecodesUntilEos()
        {
            var engine = CreateEngine(
                beamWidth: 1,
                maxLength: 8,
                handler: (source, generated, maxCandidates, cancellationToken) =>
                {
                    var last = generated[generated.Count - 1];
                    if (last == Start) return Candidates(new TranslationTokenCandidate(10, -0.1));
                    if (last == 10) return Candidates(new TranslationTokenCandidate(11, -0.1));
                    return Candidates(new TranslationTokenCandidate(Eos, -0.1));
                });

            var translated = await engine.TranslateAsync("hello", "hello");

            Assert.Equal("10,11", translated);
        }

        [Fact]
        public async Task BeamSearchKeepsGloballyBetterNonGreedySequence()
        {
            var engine = CreateEngine(
                beamWidth: 2,
                maxLength: 6,
                handler: (source, generated, maxCandidates, cancellationToken) =>
                {
                    var last = generated[generated.Count - 1];
                    if (last == Start)
                    {
                        return Candidates(
                            new TranslationTokenCandidate(10, -0.01),
                            new TranslationTokenCandidate(20, -0.10));
                    }

                    if (last == 10)
                        return Candidates(new TranslationTokenCandidate(Eos, -8.0));
                    if (last == 20)
                        return Candidates(new TranslationTokenCandidate(21, -0.01));
                    return Candidates(new TranslationTokenCandidate(Eos, -0.01));
                });

            var translated = await engine.TranslateAsync("ambiguous", "ambiguous context");

            Assert.Equal("20,21", translated);
        }

        [Fact]
        public async Task PaddingCandidateIsNotEmitted()
        {
            var engine = CreateEngine(
                beamWidth: 1,
                maxLength: 5,
                handler: (source, generated, maxCandidates, cancellationToken) =>
                {
                    var last = generated[generated.Count - 1];
                    if (last == Start)
                    {
                        return Candidates(
                            new TranslationTokenCandidate(Pad, -0.001),
                            new TranslationTokenCandidate(42, -0.2));
                    }
                    return Candidates(new TranslationTokenCandidate(Eos, -0.1));
                });

            var translated = await engine.TranslateAsync("pad", "pad");

            Assert.Equal("42", translated);
        }

        [Fact]
        public async Task MaxLengthReturnsBestPartialInsteadOfLoopingForever()
        {
            var engine = CreateEngine(
                beamWidth: 1,
                maxLength: 3,
                handler: (source, generated, maxCandidates, cancellationToken) =>
                {
                    var token = generated.Count == 1 ? 10 : 11;
                    return Candidates(new TranslationTokenCandidate(token, -0.1));
                });

            var translated = await engine.TranslateAsync("long", "long");

            Assert.Equal("10,11", translated);
        }

        [Fact]
        public async Task EmptyBackendResultFailsClosed()
        {
            var engine = CreateEngine(
                beamWidth: 1,
                maxLength: 5,
                handler: (source, generated, maxCandidates, cancellationToken) =>
                    Task.FromResult<IReadOnlyList<TranslationTokenCandidate>>(
                        Array.Empty<TranslationTokenCandidate>()));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.TranslateAsync("hello", "hello"));

            Assert.Contains("no next-token candidates", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CancellationIsObservedBeforeBackendInference()
        {
            var calls = 0;
            var engine = CreateEngine(
                beamWidth: 1,
                maxLength: 5,
                handler: (source, generated, maxCandidates, cancellationToken) =>
                {
                    calls++;
                    return Candidates(new TranslationTokenCandidate(Eos, -0.1));
                });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => engine.TranslateAsync("hello", "hello", cancellation.Token));

            Assert.Equal(0, calls);
        }

        [Fact]
        public void CandidateRejectsNonLogProbabilityScores()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationTokenCandidate(1, double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationTokenCandidate(1, double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationTokenCandidate(1, 0.01));
        }

        private static AutoregressiveTranslationEngine CreateEngine(
            int beamWidth,
            int maxLength,
            Func<IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken,
                Task<IReadOnlyList<TranslationTokenCandidate>>> handler)
        {
            return new AutoregressiveTranslationEngine(
                new EchoTokenIdTokenizer(),
                new ScriptedBackend(handler),
                new TranslationGenerationOptions(
                    decoderStartTokenId: Start,
                    eosTokenId: Eos,
                    padTokenId: Pad,
                    maxLength: maxLength,
                    beamWidth: beamWidth,
                    lengthPenalty: 1.0));
        }

        private static Task<IReadOnlyList<TranslationTokenCandidate>> Candidates(
            params TranslationTokenCandidate[] candidates)
        {
            return Task.FromResult<IReadOnlyList<TranslationTokenCandidate>>(candidates);
        }

        private sealed class EchoTokenIdTokenizer : ITranslationTokenizer
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

        private sealed class ScriptedBackend : IAutoregressiveTranslationBackend
        {
            private readonly Func<IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken,
                Task<IReadOnlyList<TranslationTokenCandidate>>> handler;

            public ScriptedBackend(
                Func<IReadOnlyList<int>, IReadOnlyList<int>, int, CancellationToken,
                    Task<IReadOnlyList<TranslationTokenCandidate>>> handler)
            {
                this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
            }

            public Task<IReadOnlyList<TranslationTokenCandidate>> PredictNextAsync(
                IReadOnlyList<int> sourceTokenIds,
                IReadOnlyList<int> generatedTokenIds,
                int maxCandidates,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                return handler(sourceTokenIds, generatedTokenIds, maxCandidates, cancellationToken);
            }
        }
    }
}
