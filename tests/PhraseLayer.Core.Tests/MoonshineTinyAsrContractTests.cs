using System;
using PhraseLayer.Core.Audio;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class MoonshineTinyAsrContractTests
    {
        [Fact]
        public void ReviewedCandidateMetadataPasses()
        {
            MoonshineTinyAsrContract.Validate(ReviewedMetadata());
        }

        [Fact]
        public void ContractPinsImmutableCandidateAndAudioRate()
        {
            Assert.Equal("moonshine-ai/moonshine-tiny", MoonshineTinyAsrContract.ModelId);
            Assert.Equal("390624ed33d594443aa4aa221f5b9f283b545b5a", MoonshineTinyAsrContract.Revision);
            Assert.Equal(40, MoonshineTinyAsrContract.Revision.Length);
            Assert.Equal(16000, MoonshineTinyAsrContract.RequiredSampleRate);
            Assert.Equal(32768, MoonshineTinyAsrContract.VocabularySize);
            Assert.Equal(194, MoonshineTinyAsrContract.MaximumGenerationLength);
        }

        [Fact]
        public void SampleRateDriftFailsBeforeInference()
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                MoonshineTinyAsrContract.Validate(ReviewedMetadata(requiredSampleRate: 48000)));

            Assert.Contains("sample-rate", error.Message);
        }

        [Fact]
        public void ArchitectureDriftFailsBeforeInference()
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                MoonshineTinyAsrContract.Validate(ReviewedMetadata(architecture: "WhisperForConditionalGeneration")));

            Assert.Contains("architecture", error.Message);
        }

        [Fact]
        public void InputNormalizationDriftFailsBeforeInference()
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                MoonshineTinyAsrContract.Validate(ReviewedMetadata(normalizeInputWaveform: true)));

            Assert.Contains("normalization", error.Message);
        }

        [Fact]
        public void CacheOrTokenDriftFailsBeforeInference()
        {
            Assert.Throws<InvalidOperationException>(() =>
                MoonshineTinyAsrContract.Validate(ReviewedMetadata(useCache: false)));
            Assert.Throws<InvalidOperationException>(() =>
                MoonshineTinyAsrContract.Validate(ReviewedMetadata(eosTokenId: 3)));
        }

        private static MoonshineTinyAsrMetadata ReviewedMetadata(
            string architecture = "MoonshineForConditionalGeneration",
            int requiredSampleRate = 16000,
            bool normalizeInputWaveform = false,
            bool useCache = true,
            int eosTokenId = 2)
        {
            return new MoonshineTinyAsrMetadata(
                architecture,
                "moonshine",
                isEncoderDecoder: true,
                useCache: useCache,
                vocabularySize: 32768,
                hiddenSize: 288,
                encoderLayerCount: 6,
                decoderLayerCount: 6,
                encoderAttentionHeadCount: 8,
                decoderAttentionHeadCount: 8,
                maximumPositionEmbeddings: 194,
                requiredSampleRate: requiredSampleRate,
                normalizeInputWaveform: normalizeInputWaveform,
                returnAttentionMask: true,
                bosTokenId: 1,
                decoderStartTokenId: 1,
                eosTokenId: eosTokenId,
                padTokenId: 2,
                maximumGenerationLength: 194);
        }
    }
}
