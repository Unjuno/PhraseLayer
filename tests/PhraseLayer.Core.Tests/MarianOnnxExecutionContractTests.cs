using System;
using System.Collections.Generic;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class MarianOnnxExecutionContractTests
    {
        [Fact]
        public void ReviewedInputSetIsExecutableByCurrentBackend()
        {
            var report = BuildReport(extraWithPastInput: null, extraWithPastOutput: null);

            OpusMtEnJaMarianOnnxExecutionContract.ValidateSupportedInputs(report);
        }

        [Fact]
        public void ExtraRequiredInputIsRejectedUntilBackendBindsIt()
        {
            var report = BuildReport(extraWithPastInput: "cache_position", extraWithPastOutput: null);

            var error = Assert.Throws<NotSupportedException>(() =>
                OpusMtEnJaMarianOnnxExecutionContract.ValidateSupportedInputs(report));

            Assert.Contains("cache_position", error.Message);
            Assert.Contains("does not bind", error.Message);
        }

        [Fact]
        public void ExtraOutputDoesNotPreventExecution()
        {
            var report = BuildReport(extraWithPastInput: null, extraWithPastOutput: "diagnostic_output");

            OpusMtEnJaMarianOnnxExecutionContract.ValidateSupportedInputs(report);
        }

        private static MarianOnnxBundleContractReport BuildReport(
            string? extraWithPastInput,
            string? extraWithPastOutput)
        {
            var encoder = new MarianOnnxGraphSignature(
                "encoder_model.onnx",
                new[]
                {
                    Tensor("input_ids", MarianOnnxTensorElementType.Integer, 2),
                    Tensor("attention_mask", MarianOnnxTensorElementType.Integer, 2)
                },
                new[] { Tensor("last_hidden_state", MarianOnnxTensorElementType.Float, 3) });

            var decoderOutputs = new List<MarianOnnxTensorSignature>
            {
                Tensor("logits", MarianOnnxTensorElementType.Float, 3)
            };
            var decoderWithPastInputs = new List<MarianOnnxTensorSignature>
            {
                Tensor("input_ids", MarianOnnxTensorElementType.Integer, 2),
                Tensor("encoder_hidden_states", MarianOnnxTensorElementType.Float, 3),
                Tensor("encoder_attention_mask", MarianOnnxTensorElementType.Integer, 2)
            };
            var decoderWithPastOutputs = new List<MarianOnnxTensorSignature>
            {
                Tensor("logits", MarianOnnxTensorElementType.Float, 3)
            };

            for (var layer = 0; layer < 6; layer++)
            {
                decoderOutputs.Add(Tensor($"present.{layer}.decoder.key", MarianOnnxTensorElementType.Float, 4));
                decoderOutputs.Add(Tensor($"present.{layer}.decoder.value", MarianOnnxTensorElementType.Float, 4));
                decoderOutputs.Add(Tensor($"present.{layer}.encoder.key", MarianOnnxTensorElementType.Float, 4));
                decoderOutputs.Add(Tensor($"present.{layer}.encoder.value", MarianOnnxTensorElementType.Float, 4));

                decoderWithPastInputs.Add(Tensor($"past_key_values.{layer}.decoder.key", MarianOnnxTensorElementType.Float, 4));
                decoderWithPastInputs.Add(Tensor($"past_key_values.{layer}.decoder.value", MarianOnnxTensorElementType.Float, 4));
                decoderWithPastInputs.Add(Tensor($"past_key_values.{layer}.encoder.key", MarianOnnxTensorElementType.Float, 4));
                decoderWithPastInputs.Add(Tensor($"past_key_values.{layer}.encoder.value", MarianOnnxTensorElementType.Float, 4));
                decoderWithPastOutputs.Add(Tensor($"present.{layer}.decoder.key", MarianOnnxTensorElementType.Float, 4));
                decoderWithPastOutputs.Add(Tensor($"present.{layer}.decoder.value", MarianOnnxTensorElementType.Float, 4));
            }

            if (extraWithPastInput != null)
                decoderWithPastInputs.Add(Tensor(extraWithPastInput, MarianOnnxTensorElementType.Integer, 1));
            if (extraWithPastOutput != null)
                decoderWithPastOutputs.Add(Tensor(extraWithPastOutput, MarianOnnxTensorElementType.Float, 1));

            var decoder = new MarianOnnxGraphSignature(
                "decoder_model.onnx",
                new[]
                {
                    Tensor("input_ids", MarianOnnxTensorElementType.Integer, 2),
                    Tensor("encoder_hidden_states", MarianOnnxTensorElementType.Float, 3),
                    Tensor("encoder_attention_mask", MarianOnnxTensorElementType.Integer, 2)
                },
                decoderOutputs);

            var decoderWithPast = new MarianOnnxGraphSignature(
                "decoder_with_past_model.onnx",
                decoderWithPastInputs,
                decoderWithPastOutputs);

            return OpusMtEnJaMarianOnnxContract.ValidateBundle(encoder, decoder, decoderWithPast);
        }

        private static MarianOnnxTensorSignature Tensor(
            string name,
            MarianOnnxTensorElementType type,
            int rank)
        {
            return new MarianOnnxTensorSignature(name, type, rank);
        }
    }
}
