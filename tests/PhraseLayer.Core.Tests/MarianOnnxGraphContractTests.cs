using System;
using System.Collections.Generic;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class MarianOnnxGraphContractTests
    {
        [Fact]
        public void ReviewedThreeFileBundlePassesStrictCacheContract()
        {
            var report = OpusMtEnJaMarianOnnxContract.ValidateBundle(
                BuildEncoder(),
                BuildDecoder(includeCrossOutputs: true),
                BuildDecoderWithPast(includeCrossOutputs: false));

            Assert.False(report.DecoderWithPastReturnsCrossAttentionCache);
            Assert.Contains("layers=6", report.ToString());
        }

        [Fact]
        public void DecoderWithPastMayReturnCrossAttentionCacheWhenExporterKeepsIt()
        {
            var report = OpusMtEnJaMarianOnnxContract.ValidateBundle(
                BuildEncoder(),
                BuildDecoder(includeCrossOutputs: true),
                BuildDecoderWithPast(includeCrossOutputs: true));

            Assert.True(report.DecoderWithPastReturnsCrossAttentionCache);
        }

        [Fact]
        public void MissingInitialDecoderCrossAttentionCacheFails()
        {
            var decoder = BuildDecoder(includeCrossOutputs: true);
            var outputs = new List<MarianOnnxTensorSignature>(decoder.Outputs);
            outputs.RemoveAll(tensor => tensor.Name == "present.3.encoder.value");

            var error = Assert.Throws<InvalidOperationException>(() =>
                OpusMtEnJaMarianOnnxContract.ValidateBundle(
                    BuildEncoder(),
                    new MarianOnnxGraphSignature("decoder_model.onnx", decoder.Inputs, outputs),
                    BuildDecoderWithPast(includeCrossOutputs: false)));

            Assert.Contains("present.3.encoder.value", error.Message);
        }

        [Fact]
        public void MissingPastCacheInputFailsBeforeRuntimeUse()
        {
            var decoderWithPast = BuildDecoderWithPast(includeCrossOutputs: false);
            var inputs = new List<MarianOnnxTensorSignature>(decoderWithPast.Inputs);
            inputs.RemoveAll(tensor => tensor.Name == "past_key_values.5.decoder.key");

            var error = Assert.Throws<InvalidOperationException>(() =>
                OpusMtEnJaMarianOnnxContract.ValidateBundle(
                    BuildEncoder(),
                    BuildDecoder(includeCrossOutputs: true),
                    new MarianOnnxGraphSignature("decoder_with_past_model.onnx", inputs, decoderWithPast.Outputs)));

            Assert.Contains("past_key_values.5.decoder.key", error.Message);
        }

        [Fact]
        public void KnownWrongInputTypeFailsLoudly()
        {
            var encoder = new MarianOnnxGraphSignature(
                "encoder_model.onnx",
                new[]
                {
                    Tensor("input_ids", MarianOnnxTensorElementType.Float, 2),
                    Tensor("attention_mask", MarianOnnxTensorElementType.Integer, 2)
                },
                new[] { Tensor("last_hidden_state", MarianOnnxTensorElementType.Float, 3) });

            var error = Assert.Throws<InvalidOperationException>(() =>
                OpusMtEnJaMarianOnnxContract.ValidateBundle(
                    encoder,
                    BuildDecoder(includeCrossOutputs: true),
                    BuildDecoderWithPast(includeCrossOutputs: false)));

            Assert.Contains("type expected Integer", error.Message);
        }

        [Fact]
        public void KnownWrongRankFailsLoudly()
        {
            var encoder = new MarianOnnxGraphSignature(
                "encoder_model.onnx",
                new[]
                {
                    Tensor("input_ids", MarianOnnxTensorElementType.Integer, 3),
                    Tensor("attention_mask", MarianOnnxTensorElementType.Integer, 2)
                },
                new[] { Tensor("last_hidden_state", MarianOnnxTensorElementType.Float, 3) });

            var error = Assert.Throws<InvalidOperationException>(() =>
                OpusMtEnJaMarianOnnxContract.ValidateBundle(
                    encoder,
                    BuildDecoder(includeCrossOutputs: true),
                    BuildDecoderWithPast(includeCrossOutputs: false)));

            Assert.Contains("rank expected 2", error.Message);
        }

        [Fact]
        public void UnknownStaticMetadataIsAllowedForHostApisThatDoNotExposeOutputTypes()
        {
            var encoder = new MarianOnnxGraphSignature(
                "encoder_model.onnx",
                new[]
                {
                    Tensor("input_ids", MarianOnnxTensorElementType.Unknown, null),
                    Tensor("attention_mask", MarianOnnxTensorElementType.Unknown, null)
                },
                new[] { Tensor("last_hidden_state", MarianOnnxTensorElementType.Unknown, null) });

            var report = OpusMtEnJaMarianOnnxContract.ValidateBundle(
                encoder,
                BuildDecoder(includeCrossOutputs: true, unknownMetadata: true),
                BuildDecoderWithPast(includeCrossOutputs: false, unknownMetadata: true));

            Assert.NotNull(report);
        }

        [Fact]
        public void PartialCrossAttentionOutputsFromWithPastFail()
        {
            var decoderWithPast = BuildDecoderWithPast(includeCrossOutputs: true);
            var outputs = new List<MarianOnnxTensorSignature>(decoderWithPast.Outputs);
            outputs.RemoveAll(tensor => tensor.Name == "present.4.encoder.value");

            var error = Assert.Throws<InvalidOperationException>(() =>
                OpusMtEnJaMarianOnnxContract.ValidateBundle(
                    BuildEncoder(),
                    BuildDecoder(includeCrossOutputs: true),
                    new MarianOnnxGraphSignature("decoder_with_past_model.onnx", decoderWithPast.Inputs, outputs)));

            Assert.Contains("cross-attention cache output pair", error.Message);
        }

        [Fact]
        public void UnexpectedCacheLayerFailsInsteadOfSilentlyAcceptingModelDrift()
        {
            var decoderWithPast = BuildDecoderWithPast(includeCrossOutputs: false);
            var inputs = new List<MarianOnnxTensorSignature>(decoderWithPast.Inputs)
            {
                Tensor("past_key_values.6.decoder.key", MarianOnnxTensorElementType.Float, 4)
            };

            var error = Assert.Throws<InvalidOperationException>(() =>
                OpusMtEnJaMarianOnnxContract.ValidateBundle(
                    BuildEncoder(),
                    BuildDecoder(includeCrossOutputs: true),
                    new MarianOnnxGraphSignature("decoder_with_past_model.onnx", inputs, decoderWithPast.Outputs)));

            Assert.Contains("outside reviewed layer range", error.Message);
        }

        [Fact]
        public void DuplicateTensorNamesAreRejectedAtSignatureConstruction()
        {
            Assert.Throws<ArgumentException>(() =>
                new MarianOnnxGraphSignature(
                    "encoder_model.onnx",
                    new[]
                    {
                        Tensor("input_ids", MarianOnnxTensorElementType.Integer, 2),
                        Tensor("input_ids", MarianOnnxTensorElementType.Integer, 2)
                    },
                    Array.Empty<MarianOnnxTensorSignature>()));
        }

        private static MarianOnnxGraphSignature BuildEncoder()
        {
            return new MarianOnnxGraphSignature(
                "encoder_model.onnx",
                new[]
                {
                    Tensor("input_ids", MarianOnnxTensorElementType.Integer, 2),
                    Tensor("attention_mask", MarianOnnxTensorElementType.Integer, 2)
                },
                new[] { Tensor("last_hidden_state", MarianOnnxTensorElementType.Float, 3) });
        }

        private static MarianOnnxGraphSignature BuildDecoder(
            bool includeCrossOutputs,
            bool unknownMetadata = false)
        {
            var integerType = unknownMetadata ? MarianOnnxTensorElementType.Unknown : MarianOnnxTensorElementType.Integer;
            var floatType = unknownMetadata ? MarianOnnxTensorElementType.Unknown : MarianOnnxTensorElementType.Float;
            int? rank2 = unknownMetadata ? null : 2;
            int? rank3 = unknownMetadata ? null : 3;
            int? rank4 = unknownMetadata ? null : 4;

            var inputs = new List<MarianOnnxTensorSignature>
            {
                Tensor("input_ids", integerType, rank2),
                Tensor("encoder_hidden_states", floatType, rank3),
                Tensor("encoder_attention_mask", integerType, rank2)
            };
            var outputs = new List<MarianOnnxTensorSignature>
            {
                Tensor("logits", floatType, rank3)
            };

            for (var layer = 0; layer < 6; layer++)
            {
                outputs.Add(Tensor($"present.{layer}.decoder.key", floatType, rank4));
                outputs.Add(Tensor($"present.{layer}.decoder.value", floatType, rank4));
                if (includeCrossOutputs)
                {
                    outputs.Add(Tensor($"present.{layer}.encoder.key", floatType, rank4));
                    outputs.Add(Tensor($"present.{layer}.encoder.value", floatType, rank4));
                }
            }

            return new MarianOnnxGraphSignature("decoder_model.onnx", inputs, outputs);
        }

        private static MarianOnnxGraphSignature BuildDecoderWithPast(
            bool includeCrossOutputs,
            bool unknownMetadata = false)
        {
            var integerType = unknownMetadata ? MarianOnnxTensorElementType.Unknown : MarianOnnxTensorElementType.Integer;
            var floatType = unknownMetadata ? MarianOnnxTensorElementType.Unknown : MarianOnnxTensorElementType.Float;
            int? rank2 = unknownMetadata ? null : 2;
            int? rank3 = unknownMetadata ? null : 3;
            int? rank4 = unknownMetadata ? null : 4;

            var inputs = new List<MarianOnnxTensorSignature>
            {
                Tensor("input_ids", integerType, rank2),
                Tensor("encoder_hidden_states", floatType, rank3),
                Tensor("encoder_attention_mask", integerType, rank2)
            };
            var outputs = new List<MarianOnnxTensorSignature>
            {
                Tensor("logits", floatType, rank3)
            };

            for (var layer = 0; layer < 6; layer++)
            {
                inputs.Add(Tensor($"past_key_values.{layer}.decoder.key", floatType, rank4));
                inputs.Add(Tensor($"past_key_values.{layer}.decoder.value", floatType, rank4));
                inputs.Add(Tensor($"past_key_values.{layer}.encoder.key", floatType, rank4));
                inputs.Add(Tensor($"past_key_values.{layer}.encoder.value", floatType, rank4));

                outputs.Add(Tensor($"present.{layer}.decoder.key", floatType, rank4));
                outputs.Add(Tensor($"present.{layer}.decoder.value", floatType, rank4));
                if (includeCrossOutputs)
                {
                    outputs.Add(Tensor($"present.{layer}.encoder.key", floatType, rank4));
                    outputs.Add(Tensor($"present.{layer}.encoder.value", floatType, rank4));
                }
            }

            return new MarianOnnxGraphSignature("decoder_with_past_model.onnx", inputs, outputs);
        }

        private static MarianOnnxTensorSignature Tensor(
            string name,
            MarianOnnxTensorElementType type,
            int? rank)
        {
            return new MarianOnnxTensorSignature(name, type, rank);
        }
    }
}
