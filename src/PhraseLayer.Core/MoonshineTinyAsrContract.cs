using System;

namespace PhraseLayer.Core.Audio
{
    /// <summary>
    /// Reviewed metadata for the English Moonshine Tiny ASR candidate. This is a runtime-neutral
    /// compatibility contract only: it does not download weights, depend on Transformers, or assert
    /// Unity/Quest execution. Concrete backends must still validate their exported graph signatures.
    /// </summary>
    public static class MoonshineTinyAsrContract
    {
        public const string ModelId = "moonshine-ai/moonshine-tiny";
        public const string Revision = "390624ed33d594443aa4aa221f5b9f283b545b5a";
        public const string Architecture = "MoonshineForConditionalGeneration";
        public const string ModelType = "moonshine";
        public const string Language = "en";
        public const string License = "mit";

        public const int RequiredSampleRate = 16000;
        public const int VocabularySize = 32768;
        public const int HiddenSize = 288;
        public const int EncoderLayerCount = 6;
        public const int DecoderLayerCount = 6;
        public const int EncoderAttentionHeadCount = 8;
        public const int DecoderAttentionHeadCount = 8;
        // Deployment graph helpers use the shorter plural form. Keep it as an exact alias so
        // graph/runtime code cannot drift from the reviewed model metadata constant above.
        public const int DecoderAttentionHeads = DecoderAttentionHeadCount;
        public const int MaximumPositionEmbeddings = 194;
        public const int MaximumGenerationLength = 194;
        public const int BosTokenId = 1;
        public const int DecoderStartTokenId = 1;
        public const int EosTokenId = 2;
        public const int PadTokenId = 2;

        public static void Validate(MoonshineTinyAsrMetadata metadata)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            Require(metadata.Architecture == Architecture, "Moonshine architecture drift.");
            Require(metadata.ModelType == ModelType, "Moonshine model_type drift.");
            Require(metadata.IsEncoderDecoder, "Moonshine must remain an encoder-decoder model.");
            Require(metadata.UseCache, "Moonshine decoder cache must remain enabled.");
            Require(metadata.VocabularySize == VocabularySize, "Moonshine vocabulary-size drift.");
            Require(metadata.HiddenSize == HiddenSize, "Moonshine hidden-size drift.");
            Require(metadata.EncoderLayerCount == EncoderLayerCount, "Moonshine encoder-layer drift.");
            Require(metadata.DecoderLayerCount == DecoderLayerCount, "Moonshine decoder-layer drift.");
            Require(metadata.EncoderAttentionHeadCount == EncoderAttentionHeadCount, "Moonshine encoder-head drift.");
            Require(metadata.DecoderAttentionHeadCount == DecoderAttentionHeadCount, "Moonshine decoder-head drift.");
            Require(metadata.MaximumPositionEmbeddings == MaximumPositionEmbeddings, "Moonshine position-limit drift.");
            Require(metadata.RequiredSampleRate == RequiredSampleRate, "Moonshine sample-rate drift.");
            Require(!metadata.NormalizeInputWaveform, "Moonshine preprocessor normalization drift.");
            Require(metadata.ReturnAttentionMask, "Moonshine must retain the input attention mask.");
            Require(metadata.BosTokenId == BosTokenId, "Moonshine BOS drift.");
            Require(metadata.DecoderStartTokenId == DecoderStartTokenId, "Moonshine decoder-start drift.");
            Require(metadata.EosTokenId == EosTokenId, "Moonshine EOS drift.");
            Require(metadata.PadTokenId == PadTokenId, "Moonshine PAD drift.");
            Require(metadata.MaximumGenerationLength == MaximumGenerationLength, "Moonshine generation-length drift.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    public sealed class MoonshineTinyAsrMetadata
    {
        public MoonshineTinyAsrMetadata(
            string architecture,
            string modelType,
            bool isEncoderDecoder,
            bool useCache,
            int vocabularySize,
            int hiddenSize,
            int encoderLayerCount,
            int decoderLayerCount,
            int encoderAttentionHeadCount,
            int decoderAttentionHeadCount,
            int maximumPositionEmbeddings,
            int requiredSampleRate,
            bool normalizeInputWaveform,
            bool returnAttentionMask,
            int bosTokenId,
            int decoderStartTokenId,
            int eosTokenId,
            int padTokenId,
            int maximumGenerationLength)
        {
            Architecture = architecture ?? throw new ArgumentNullException(nameof(architecture));
            ModelType = modelType ?? throw new ArgumentNullException(nameof(modelType));
            IsEncoderDecoder = isEncoderDecoder;
            UseCache = useCache;
            VocabularySize = vocabularySize;
            HiddenSize = hiddenSize;
            EncoderLayerCount = encoderLayerCount;
            DecoderLayerCount = decoderLayerCount;
            EncoderAttentionHeadCount = encoderAttentionHeadCount;
            DecoderAttentionHeadCount = decoderAttentionHeadCount;
            MaximumPositionEmbeddings = maximumPositionEmbeddings;
            RequiredSampleRate = requiredSampleRate;
            NormalizeInputWaveform = normalizeInputWaveform;
            ReturnAttentionMask = returnAttentionMask;
            BosTokenId = bosTokenId;
            DecoderStartTokenId = decoderStartTokenId;
            EosTokenId = eosTokenId;
            PadTokenId = padTokenId;
            MaximumGenerationLength = maximumGenerationLength;
        }

        public string Architecture { get; }
        public string ModelType { get; }
        public bool IsEncoderDecoder { get; }
        public bool UseCache { get; }
        public int VocabularySize { get; }
        public int HiddenSize { get; }
        public int EncoderLayerCount { get; }
        public int DecoderLayerCount { get; }
        public int EncoderAttentionHeadCount { get; }
        public int DecoderAttentionHeadCount { get; }
        public int MaximumPositionEmbeddings { get; }
        public int RequiredSampleRate { get; }
        public bool NormalizeInputWaveform { get; }
        public bool ReturnAttentionMask { get; }
        public int BosTokenId { get; }
        public int DecoderStartTokenId { get; }
        public int EosTokenId { get; }
        public int PadTokenId { get; }
        public int MaximumGenerationLength { get; }
    }
}
