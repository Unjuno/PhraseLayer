using System;

namespace PhraseLayer.Core.Translation
{
    public sealed class MarianTranslationMetadata
    {
        public MarianTranslationMetadata(
            string modelType,
            string architecture,
            string sourceLanguage,
            string targetLanguage,
            int vocabularySize,
            int decoderVocabularySize,
            int modelDimension,
            int encoderLayers,
            int decoderLayers,
            int maximumPositionEmbeddings,
            int bosTokenId,
            int eosTokenId,
            int padTokenId,
            int decoderStartTokenId,
            int configuredBeamWidth)
        {
            ModelType = modelType ?? throw new ArgumentNullException(nameof(modelType));
            Architecture = architecture ?? throw new ArgumentNullException(nameof(architecture));
            SourceLanguage = sourceLanguage ?? throw new ArgumentNullException(nameof(sourceLanguage));
            TargetLanguage = targetLanguage ?? throw new ArgumentNullException(nameof(targetLanguage));
            VocabularySize = vocabularySize;
            DecoderVocabularySize = decoderVocabularySize;
            ModelDimension = modelDimension;
            EncoderLayers = encoderLayers;
            DecoderLayers = decoderLayers;
            MaximumPositionEmbeddings = maximumPositionEmbeddings;
            BosTokenId = bosTokenId;
            EosTokenId = eosTokenId;
            PadTokenId = padTokenId;
            DecoderStartTokenId = decoderStartTokenId;
            ConfiguredBeamWidth = configuredBeamWidth;
        }

        public string ModelType { get; }
        public string Architecture { get; }
        public string SourceLanguage { get; }
        public string TargetLanguage { get; }
        public int VocabularySize { get; }
        public int DecoderVocabularySize { get; }
        public int ModelDimension { get; }
        public int EncoderLayers { get; }
        public int DecoderLayers { get; }
        public int MaximumPositionEmbeddings { get; }
        public int BosTokenId { get; }
        public int EosTokenId { get; }
        public int PadTokenId { get; }
        public int DecoderStartTokenId { get; }
        public int ConfiguredBeamWidth { get; }
    }

    public sealed class MarianTranslationContractReport
    {
        internal MarianTranslationContractReport(MarianTranslationMetadata metadata)
        {
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        }

        public MarianTranslationMetadata Metadata { get; }

        public override string ToString()
        {
            return string.Format(
                "Marian {0}->{1}; vocab={2}; d_model={3}; encoder_layers={4}; decoder_layers={5}; max_positions={6}; eos={7}; pad/start={8}; configured_beams={9}",
                Metadata.SourceLanguage,
                Metadata.TargetLanguage,
                Metadata.VocabularySize,
                Metadata.ModelDimension,
                Metadata.EncoderLayers,
                Metadata.DecoderLayers,
                Metadata.MaximumPositionEmbeddings,
                Metadata.EosTokenId,
                Metadata.PadTokenId,
                Metadata.ConfiguredBeamWidth);
        }
    }

    /// <summary>
    /// Revision-independent graph/token contract observed for Helsinki-NLP/opus-mt-en-jap.
    /// This is deliberately strict: model drift must fail before a candidate reaches the Quest runtime.
    /// The repository still requires revision-level artifact hashes and a real Unity/Quest import before
    /// claiming runtime compatibility.
    /// </summary>
    public static class OpusMtEnJaMarianContract
    {
        public const string ExpectedModelType = "marian";
        public const string ExpectedArchitecture = "MarianMTModel";
        public const string ExpectedSourceLanguage = "en";
        public const string ExpectedTargetLanguage = "jap";
        public const int ExpectedVocabularySize = 46276;
        public const int ExpectedModelDimension = 512;
        public const int ExpectedEncoderLayers = 6;
        public const int ExpectedDecoderLayers = 6;
        public const int ExpectedMaximumPositionEmbeddings = 512;
        public const int ExpectedBosTokenId = 0;
        public const int ExpectedEosTokenId = 0;
        public const int ExpectedPadTokenId = 46275;
        public const int ExpectedDecoderStartTokenId = 46275;
        public const int ExpectedConfiguredBeamWidth = 4;

        public static MarianTranslationContractReport Validate(MarianTranslationMetadata metadata)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            Require(metadata.ModelType == ExpectedModelType,
                "model_type", ExpectedModelType, metadata.ModelType);
            Require(metadata.Architecture == ExpectedArchitecture,
                "architecture", ExpectedArchitecture, metadata.Architecture);
            Require(metadata.SourceLanguage == ExpectedSourceLanguage,
                "source language", ExpectedSourceLanguage, metadata.SourceLanguage);
            Require(metadata.TargetLanguage == ExpectedTargetLanguage,
                "target language", ExpectedTargetLanguage, metadata.TargetLanguage);
            Require(metadata.VocabularySize == ExpectedVocabularySize,
                "vocab_size", ExpectedVocabularySize, metadata.VocabularySize);
            Require(metadata.DecoderVocabularySize == ExpectedVocabularySize,
                "decoder_vocab_size", ExpectedVocabularySize, metadata.DecoderVocabularySize);
            Require(metadata.ModelDimension == ExpectedModelDimension,
                "d_model", ExpectedModelDimension, metadata.ModelDimension);
            Require(metadata.EncoderLayers == ExpectedEncoderLayers,
                "encoder_layers", ExpectedEncoderLayers, metadata.EncoderLayers);
            Require(metadata.DecoderLayers == ExpectedDecoderLayers,
                "decoder_layers", ExpectedDecoderLayers, metadata.DecoderLayers);
            Require(metadata.MaximumPositionEmbeddings == ExpectedMaximumPositionEmbeddings,
                "max_position_embeddings", ExpectedMaximumPositionEmbeddings, metadata.MaximumPositionEmbeddings);
            Require(metadata.BosTokenId == ExpectedBosTokenId,
                "bos_token_id", ExpectedBosTokenId, metadata.BosTokenId);
            Require(metadata.EosTokenId == ExpectedEosTokenId,
                "eos_token_id", ExpectedEosTokenId, metadata.EosTokenId);
            Require(metadata.PadTokenId == ExpectedPadTokenId,
                "pad_token_id", ExpectedPadTokenId, metadata.PadTokenId);
            Require(metadata.DecoderStartTokenId == ExpectedDecoderStartTokenId,
                "decoder_start_token_id", ExpectedDecoderStartTokenId, metadata.DecoderStartTokenId);
            Require(metadata.ConfiguredBeamWidth == ExpectedConfiguredBeamWidth,
                "num_beams", ExpectedConfiguredBeamWidth, metadata.ConfiguredBeamWidth);

            return new MarianTranslationContractReport(metadata);
        }

        public static void ValidateGenerationOptions(TranslationGenerationOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.MaximumSourceTokens > ExpectedMaximumPositionEmbeddings)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Maximum source tokens exceed the reviewed Marian positional-embedding limit.");
            }
            if (options.MaximumTargetTokens > ExpectedMaximumPositionEmbeddings)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Maximum target tokens exceed the reviewed Marian generation limit.");
            }
        }

        private static void Require(bool condition, string field, object expected, object actual)
        {
            if (!condition)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "OPUS-MT en-ja Marian contract drift: {0} expected {1} but found {2}.",
                        field,
                        expected,
                        actual));
            }
        }
    }
}
