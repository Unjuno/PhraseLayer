using System;

namespace PhraseLayer.Core.Translation
{
    /// <summary>
    /// SentencePiece metadata measured from the exact tokenizer artifacts emitted by the pinned OPUS-MT export.
    /// These values are an input contract for local staging and the managed tokenizer; they are not a claim that
    /// the managed nmt_nfkc implementation is parity-complete. ParityVerifiedTranslationTokenizer remains the
    /// promotion gate for actual tokenization behavior.
    /// </summary>
    public static class OpusMtEnJapSentencePieceContract
    {
        public const string ModelType = "UNIGRAM";
        public const string NormalizerName = "nmt_nfkc";
        public const string NormalizerCharsMapSha256 =
            "cab969cc39d743f8402e6fd752a0916e71839bcb27171ca72191336b7f71b4bc";

        public const int SourcePieceCount = 15882;
        public const int TargetPieceCount = 32000;
        public const int SentencePieceUnknownId = 0;
        public const int SentencePieceBosId = 1;
        public const int SentencePieceEosId = 2;
        public const int SentencePiecePadId = -1;

        public const bool ByteFallback = false;
        public const bool SplitByUnicodeScript = true;
        public const bool SplitByNumber = true;
        public const bool SplitByWhitespace = true;
        public const bool SplitDigits = false;
        public const bool TreatWhitespaceAsSuffix = false;
        public const bool AllowWhitespaceOnlyPieces = false;
        public const bool AddDummyPrefix = true;
        public const bool RemoveExtraWhitespaces = true;
        public const bool EscapeWhitespaces = true;

        public static void ValidateMeasuredManifest(SentencePieceRuntimeManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (!string.Equals(manifest.ModelType, ModelType, StringComparison.Ordinal))
                throw new InvalidOperationException("Unsupported SentencePiece model type: " + manifest.ModelType);
            if (!string.Equals(manifest.NormalizerName, NormalizerName, StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected SentencePiece normalizer: " + manifest.NormalizerName);
            if (!string.Equals(manifest.NormalizerCharsMapSha256, NormalizerCharsMapSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("SentencePiece normalizer charsmap identity drift.");
            if (manifest.SourcePieceCount != SourcePieceCount || manifest.TargetPieceCount != TargetPieceCount)
                throw new InvalidOperationException(
                    "SentencePiece piece-count drift: expected " + SourcePieceCount + "/" + TargetPieceCount +
                    " actual " + manifest.SourcePieceCount + "/" + manifest.TargetPieceCount + ".");
            if (manifest.ByteFallback != ByteFallback ||
                manifest.AddDummyPrefix != AddDummyPrefix ||
                manifest.RemoveExtraWhitespaces != RemoveExtraWhitespaces ||
                manifest.EscapeWhitespaces != EscapeWhitespaces)
            {
                throw new InvalidOperationException("SentencePiece normalization/runtime options drift from measured export.");
            }
        }

        public static string BuildReport()
        {
            return
                "opus-mt sentencepiece" +
                " type=" + ModelType +
                " sourcePieces=" + SourcePieceCount +
                " targetPieces=" + TargetPieceCount +
                " normalizer=" + NormalizerName +
                " charsmap=" + NormalizerCharsMapSha256 +
                " byteFallback=" + ByteFallback;
        }
    }

    /// <summary>
    /// Small platform-neutral metadata view produced by the local tokenizer staging step. The large piece tables
    /// remain outside this object; this record exists so Core can reject a tokenizer bundle with the wrong
    /// measured SentencePiece semantics before constructing ManagedSentencePieceUnigramTokenizer.
    /// </summary>
    public sealed class SentencePieceRuntimeManifest
    {
        public SentencePieceRuntimeManifest(
            string modelType,
            string normalizerName,
            string normalizerCharsMapSha256,
            int sourcePieceCount,
            int targetPieceCount,
            bool byteFallback,
            bool addDummyPrefix,
            bool removeExtraWhitespaces,
            bool escapeWhitespaces)
        {
            ModelType = modelType ?? throw new ArgumentNullException(nameof(modelType));
            NormalizerName = normalizerName ?? throw new ArgumentNullException(nameof(normalizerName));
            NormalizerCharsMapSha256 = normalizerCharsMapSha256 ?? throw new ArgumentNullException(nameof(normalizerCharsMapSha256));
            if (sourcePieceCount <= 0) throw new ArgumentOutOfRangeException(nameof(sourcePieceCount));
            if (targetPieceCount <= 0) throw new ArgumentOutOfRangeException(nameof(targetPieceCount));
            SourcePieceCount = sourcePieceCount;
            TargetPieceCount = targetPieceCount;
            ByteFallback = byteFallback;
            AddDummyPrefix = addDummyPrefix;
            RemoveExtraWhitespaces = removeExtraWhitespaces;
            EscapeWhitespaces = escapeWhitespaces;
        }

        public string ModelType { get; }
        public string NormalizerName { get; }
        public string NormalizerCharsMapSha256 { get; }
        public int SourcePieceCount { get; }
        public int TargetPieceCount { get; }
        public bool ByteFallback { get; }
        public bool AddDummyPrefix { get; }
        public bool RemoveExtraWhitespaces { get; }
        public bool EscapeWhitespaces { get; }
    }
}
