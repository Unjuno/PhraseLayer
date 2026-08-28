using System;
using System.Collections.Generic;

namespace PhraseLayer.Core.Translation
{
    /// <summary>
    /// Generation semantics observed in the reviewed OPUS-MT en->ja candidate configuration.
    ///
    /// The upstream model's default generation configuration uses beam search (num_beams=4). PhraseLayer's
    /// correctness-first runtime intentionally starts with deterministic greedy generation (beamWidth=1) so the
    /// Unity cache backend can be parity-tested one sequence at a time. Reference parity therefore must explicitly
    /// force num_beams=1 in Transformers; it must not be compared against the model's default beam-4 output.
    ///
    /// The PAD token is also the decoder-start token and is listed by the candidate as a one-token bad word.
    /// PhraseLayer must ban it after the initial decoder seed instead of allowing raw argmax to emit <pad>.
    /// </summary>
    public static class OpusMtEnJaGenerationPolicy
    {
        public const int UpstreamDefaultBeamWidth = OpusMtEnJaMarianContract.ExpectedConfiguredBeamWidth;
        public const int PhraseLayerGreedyBeamWidth = 1;
        public const int BannedPadTokenId = OpusMtEnJaMarianContract.ExpectedPadTokenId;
        public const int ForcedEosTokenId = OpusMtEnJaMarianContract.ExpectedEosTokenId;
        public const bool UpstreamRenormalizeLogits = true;

        private static readonly int[] bannedTokenIds = { BannedPadTokenId };

        public static IReadOnlyList<int> BannedTokenIds => bannedTokenIds;

        public static GreedySeq2SeqTranslationModel CreateGreedyModel(ISeq2SeqGenerationBackend backend)
        {
            if (backend == null) throw new ArgumentNullException(nameof(backend));
            return new GreedySeq2SeqTranslationModel(
                backend,
                OpusMtEnJaMarianContract.ExpectedVocabularySize,
                OpusMtEnJaMarianContract.ExpectedDecoderStartTokenId,
                OpusMtEnJaMarianContract.ExpectedEosTokenId,
                bannedTokenIds,
                forceEosAtMaximumTokens: true);
        }

        public static TranslationGenerationOptions CreateGreedyParityOptions(
            int maximumSourceTokens = 128,
            int maximumTargetTokens = 128)
        {
            var options = new TranslationGenerationOptions(
                maximumSourceTokens,
                maximumTargetTokens,
                PhraseLayerGreedyBeamWidth);
            ValidateGreedyParityOptions(options);
            return options;
        }

        public static void ValidateGreedyParityOptions(TranslationGenerationOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            OpusMtEnJaMarianContract.ValidateGenerationOptions(options);
            if (options.BeamWidth != PhraseLayerGreedyBeamWidth)
            {
                throw new NotSupportedException(
                    "OPUS-MT en-ja PhraseLayer parity currently requires beamWidth=1. " +
                    "The upstream model default is beamWidth=4; compare against Transformers generate(num_beams=1, do_sample=False) " +
                    "until an independently tested beam-search generator is implemented.");
            }
        }
    }
}
