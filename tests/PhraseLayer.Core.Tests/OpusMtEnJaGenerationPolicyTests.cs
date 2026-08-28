using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class OpusMtEnJaGenerationPolicyTests
    {
        [Fact]
        public async Task GreedyFactoryBansPadDecoderStartAfterInitialSeed()
        {
            var backend = new ScriptedBackend(
                LogitsWithTopTokens(
                    OpusMtEnJaMarianContract.ExpectedPadTokenId,
                    secondTokenId: 42),
                LogitsWithTopTokens(
                    OpusMtEnJaMarianContract.ExpectedEosTokenId,
                    secondTokenId: 43));
            var model = OpusMtEnJaGenerationPolicy.CreateGreedyModel(backend);
            var options = OpusMtEnJaGenerationPolicy.CreateGreedyParityOptions(
                maximumSourceTokens: 8,
                maximumTargetTokens: 8);

            var result = await model.GenerateAsync(new[] { 10, 0 }, options);

            Assert.Equal(new[] { 42, 0 }, result.TokenIds);
            Assert.Equal(TranslationGenerationStopReason.EndOfSequence, result.StopReason);
            Assert.Equal(
                new[] { OpusMtEnJaMarianContract.ExpectedDecoderStartTokenId, 42 },
                backend.Session.PreviousTokens);
        }

        [Fact]
        public void PolicyPinsObservedCandidateGenerationSemantics()
        {
            Assert.Equal(4, OpusMtEnJaGenerationPolicy.UpstreamDefaultBeamWidth);
            Assert.Equal(1, OpusMtEnJaGenerationPolicy.PhraseLayerGreedyBeamWidth);
            Assert.Equal(46275, OpusMtEnJaGenerationPolicy.BannedPadTokenId);
            Assert.Equal(0, OpusMtEnJaGenerationPolicy.ForcedEosTokenId);
            Assert.True(OpusMtEnJaGenerationPolicy.UpstreamRenormalizeLogits);
            Assert.Equal(new[] { 46275 }, OpusMtEnJaGenerationPolicy.BannedTokenIds);
        }

        [Fact]
        public void BeamFourIsRejectedFromGreedyParityInsteadOfBeingSilentlyApproximated()
        {
            var options = new TranslationGenerationOptions(
                maximumSourceTokens: 128,
                maximumTargetTokens: 128,
                beamWidth: 4);

            var error = Assert.Throws<NotSupportedException>(() =>
                OpusMtEnJaGenerationPolicy.ValidateGreedyParityOptions(options));

            Assert.Contains("beamWidth=1", error.Message);
            Assert.Contains("beamWidth=4", error.Message);
        }

        [Fact]
        public async Task MaximumSlotForcesEosEvenWhenAnotherTokenWins()
        {
            var backend = new ScriptedBackend(LogitsWithTopTokens(77, secondTokenId: 78));
            var model = OpusMtEnJaGenerationPolicy.CreateGreedyModel(backend);
            var options = OpusMtEnJaGenerationPolicy.CreateGreedyParityOptions(
                maximumSourceTokens: 8,
                maximumTargetTokens: 1);

            var result = await model.GenerateAsync(new[] { 10, 0 }, options);

            Assert.Equal(new[] { 0 }, result.TokenIds);
            Assert.Equal(TranslationGenerationStopReason.MaximumTokens, result.StopReason);
        }

        private static IReadOnlyList<float> LogitsWithTopTokens(int highestTokenId, int secondTokenId)
        {
            var logits = new float[OpusMtEnJaMarianContract.ExpectedVocabularySize];
            logits[highestTokenId] = 10f;
            logits[secondTokenId] = 9f;
            return logits;
        }

        private sealed class ScriptedBackend : ISeq2SeqGenerationBackend
        {
            public ScriptedBackend(params IReadOnlyList<float>[] logits)
            {
                Session = new ScriptedSession(logits);
            }

            public ScriptedSession Session { get; }

            public Task<ISeq2SeqGenerationSession> StartAsync(
                IReadOnlyList<int> sourceTokenIds,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<ISeq2SeqGenerationSession>(Session);
            }
        }

        private sealed class ScriptedSession : ISeq2SeqGenerationSession
        {
            private readonly IReadOnlyList<float>[] logits;
            private int index;

            public ScriptedSession(IReadOnlyList<float>[] logits)
            {
                this.logits = logits;
                PreviousTokens = new List<int>();
            }

            public List<int> PreviousTokens { get; }

            public Task<Seq2SeqDecoderStepResult> DecodeNextAsync(
                int previousTokenId,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PreviousTokens.Add(previousTokenId);
                if (index >= logits.Length)
                    throw new InvalidOperationException("No scripted decoder step remains.");
                return Task.FromResult(new Seq2SeqDecoderStepResult(logits[index++]));
            }

            public void Dispose()
            {
            }
        }
    }
}
