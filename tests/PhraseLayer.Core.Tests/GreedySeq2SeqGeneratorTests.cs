using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class GreedySeq2SeqGeneratorTests
    {
        [Fact]
        public async Task GreedyGenerationFeedsSelectedTokenBackIntoCachedSessionUntilEos()
        {
            var backend = new RecordingBackend(
                Logits(8, (5, 9f), (0, 1f)),
                Logits(8, (0, 11f), (6, 3f)));
            var model = new GreedySeq2SeqTranslationModel(
                backend,
                vocabularySize: 8,
                decoderStartTokenId: 7,
                eosTokenId: 0,
                bannedTokenIds: new[] { 6 });

            var result = await model.GenerateAsync(
                new[] { 2, 3, 0 },
                new TranslationGenerationOptions(16, 8, 1));

            Assert.Equal(new[] { 5, 0 }, result.TokenIds);
            Assert.Equal(TranslationGenerationStopReason.EndOfSequence, result.StopReason);
            Assert.Equal(new[] { 7, 5 }, backend.Session.PreviousTokens);
            Assert.Equal(new[] { 2, 3, 0 }, backend.SourceTokens);
            Assert.True(backend.Session.WasDisposed);
        }

        [Fact]
        public async Task BannedHighestLogitIsSkipped()
        {
            var backend = new RecordingBackend(
                Logits(6, (4, 100f), (3, 10f), (0, 1f)),
                Logits(6, (0, 10f)));
            var model = new GreedySeq2SeqTranslationModel(
                backend,
                vocabularySize: 6,
                decoderStartTokenId: 5,
                eosTokenId: 0,
                bannedTokenIds: new[] { 4 });

            var result = await model.GenerateAsync(
                new[] { 1, 0 },
                new TranslationGenerationOptions(8, 4, 1));

            Assert.Equal(new[] { 3, 0 }, result.TokenIds);
        }

        [Fact]
        public async Task FinalSlotForcesEosInsteadOfReturningOpenEndedSequence()
        {
            var backend = new RecordingBackend(
                Logits(8, (5, 9f)),
                Logits(8, (6, 9f)));
            var model = new GreedySeq2SeqTranslationModel(
                backend,
                vocabularySize: 8,
                decoderStartTokenId: 7,
                eosTokenId: 0);

            var result = await model.GenerateAsync(
                new[] { 2, 0 },
                new TranslationGenerationOptions(8, 2, 1));

            Assert.Equal(new[] { 5, 0 }, result.TokenIds);
            Assert.Equal(TranslationGenerationStopReason.MaximumTokens, result.StopReason);
            Assert.Equal(2, backend.Session.PreviousTokens.Count);
            Assert.True(backend.Session.WasDisposed);
        }

        [Fact]
        public async Task BeamSearchIsRejectedRatherThanSilentlyApproximated()
        {
            var backend = new RecordingBackend(Logits(4, (0, 1f)));
            var model = new GreedySeq2SeqTranslationModel(
                backend,
                vocabularySize: 4,
                decoderStartTokenId: 3,
                eosTokenId: 0);

            await Assert.ThrowsAsync<NotSupportedException>(() =>
                model.GenerateAsync(
                    new[] { 1, 0 },
                    new TranslationGenerationOptions(8, 8, 4)));
            Assert.Null(backend.Session);
        }

        [Fact]
        public async Task VocabularyShapeDriftDisposesSessionAndFails()
        {
            var backend = new RecordingBackend(new Seq2SeqDecoderStepResult(new float[3]));
            var model = new GreedySeq2SeqTranslationModel(
                backend,
                vocabularySize: 4,
                decoderStartTokenId: 3,
                eosTokenId: 0);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                model.GenerateAsync(
                    new[] { 1, 0 },
                    new TranslationGenerationOptions(8, 8, 1)));

            Assert.Contains("vocabulary drift", error.Message);
            Assert.True(backend.Session.WasDisposed);
        }

        [Fact]
        public async Task NonFiniteLogitsFailBeforeSelectingAToken()
        {
            var values = new float[4];
            values[2] = float.NaN;
            var backend = new RecordingBackend(new Seq2SeqDecoderStepResult(values));
            var model = new GreedySeq2SeqTranslationModel(
                backend,
                vocabularySize: 4,
                decoderStartTokenId: 3,
                eosTokenId: 0);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                model.GenerateAsync(
                    new[] { 1, 0 },
                    new TranslationGenerationOptions(8, 8, 1)));

            Assert.Contains("non-finite logit", error.Message);
            Assert.True(backend.Session.WasDisposed);
        }

        private static Seq2SeqDecoderStepResult Logits(
            int vocabularySize,
            params (int tokenId, float score)[] scoredTokens)
        {
            var logits = new float[vocabularySize];
            for (var index = 0; index < logits.Length; index++) logits[index] = -100f;
            for (var index = 0; index < scoredTokens.Length; index++)
                logits[scoredTokens[index].tokenId] = scoredTokens[index].score;
            return new Seq2SeqDecoderStepResult(logits);
        }

        private sealed class RecordingBackend : ISeq2SeqGenerationBackend
        {
            private readonly IReadOnlyList<Seq2SeqDecoderStepResult> steps;

            public RecordingBackend(params Seq2SeqDecoderStepResult[] steps)
            {
                this.steps = steps;
            }

            public IReadOnlyList<int> SourceTokens { get; private set; }
            public RecordingSession Session { get; private set; }

            public Task<ISeq2SeqGenerationSession> StartAsync(
                IReadOnlyList<int> sourceTokenIds,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SourceTokens = new List<int>(sourceTokenIds);
                Session = new RecordingSession(steps);
                return Task.FromResult<ISeq2SeqGenerationSession>(Session);
            }
        }

        private sealed class RecordingSession : ISeq2SeqGenerationSession
        {
            private readonly IReadOnlyList<Seq2SeqDecoderStepResult> steps;
            private int index;

            public RecordingSession(IReadOnlyList<Seq2SeqDecoderStepResult> steps)
            {
                this.steps = steps;
            }

            public List<int> PreviousTokens { get; } = new List<int>();
            public bool WasDisposed { get; private set; }

            public Task<Seq2SeqDecoderStepResult> DecodeNextAsync(
                int previousTokenId,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PreviousTokens.Add(previousTokenId);
                if (index >= steps.Count)
                    throw new InvalidOperationException("Test decoder exhausted its scripted steps.");
                return Task.FromResult(steps[index++]);
            }

            public void Dispose()
            {
                WasDisposed = true;
            }
        }
    }
}
