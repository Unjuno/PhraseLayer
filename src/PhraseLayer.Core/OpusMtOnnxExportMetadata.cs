using System;
using System.Collections.Generic;

namespace PhraseLayer.Core.Translation
{
    public enum MeasuredOnnxElementType
    {
        Float32 = 1,
        Int64 = 7,
    }

    public sealed class MeasuredOnnxTensor
    {
        public MeasuredOnnxTensor(
            string name,
            MeasuredOnnxElementType elementType,
            params string[] dimensions)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            ElementType = elementType;
            Dimensions = dimensions ?? throw new ArgumentNullException(nameof(dimensions));
        }

        public string Name { get; }
        public MeasuredOnnxElementType ElementType { get; }
        public IReadOnlyList<string> Dimensions { get; }
    }

    public sealed class MeasuredOnnxModel
    {
        public MeasuredOnnxModel(
            string fileName,
            long sizeBytes,
            string sha256,
            int irVersion,
            int opset,
            IReadOnlyList<MeasuredOnnxTensor> inputs,
            IReadOnlyList<MeasuredOnnxTensor> outputs)
        {
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            if (sizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
            Sha256 = sha256 ?? throw new ArgumentNullException(nameof(sha256));
            if (irVersion <= 0) throw new ArgumentOutOfRangeException(nameof(irVersion));
            if (opset <= 0) throw new ArgumentOutOfRangeException(nameof(opset));
            Inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
            Outputs = outputs ?? throw new ArgumentNullException(nameof(outputs));

            SizeBytes = sizeBytes;
            IrVersion = irVersion;
            Opset = opset;
        }

        public string FileName { get; }
        public long SizeBytes { get; }
        public string Sha256 { get; }
        public int IrVersion { get; }
        public int Opset { get; }
        public IReadOnlyList<MeasuredOnnxTensor> Inputs { get; }
        public IReadOnlyList<MeasuredOnnxTensor> Outputs { get; }
    }

    /// <summary>
    /// Metadata measured by the metadata-only GitHub export/parity probe for the pinned OPUS-MT revision.
    ///
    /// The reference decoder is deliberately decoder_model.onnx rather than decoder_model_merged.onnx.
    /// The non-cached decoder has only three inputs and can rerun the complete generated prefix on each step,
    /// matching IAutoregressiveTranslationBackend's correctness-first contract. KV-cache execution remains a
    /// later Quest optimization and must not be introduced before a parity/latency measurement justifies it.
    /// </summary>
    public static class OpusMtEnJapMeasuredOnnxContract
    {
        public const string ProbeCommit = "792055c78981de4dfaf2a4b38865793005a546cb";
        public const long ReferenceRuntimeSizeBytes = 463431659;
        public const int HiddenSize = 512;
        public const int VocabularySize = 46276;

        public static readonly MeasuredOnnxModel Encoder = new MeasuredOnnxModel(
            "encoder_model.onnx",
            171553398,
            "bb0d8d22053062bbd3695a468c88d1f84367eb195fa5f9fb75aa6c9548f57c59",
            irVersion: 8,
            opset: 18,
            inputs: new[]
            {
                new MeasuredOnnxTensor("input_ids", MeasuredOnnxElementType.Int64, "batch_size", "encoder_sequence_length"),
                new MeasuredOnnxTensor("attention_mask", MeasuredOnnxElementType.Int64, "batch_size", "encoder_sequence_length"),
            },
            outputs: new[]
            {
                new MeasuredOnnxTensor("last_hidden_state", MeasuredOnnxElementType.Float32, "batch_size", "encoder_sequence_length", "512"),
            });

        public static readonly MeasuredOnnxModel Decoder = new MeasuredOnnxModel(
            "decoder_model.onnx",
            291878261,
            "513bbf05f48da69847ce247e3245a5e84a814a7e591e8f544dea4854d202dc00",
            irVersion: 8,
            opset: 18,
            inputs: new[]
            {
                new MeasuredOnnxTensor("encoder_attention_mask", MeasuredOnnxElementType.Int64, "batch_size", "encoder_sequence_length"),
                new MeasuredOnnxTensor("input_ids", MeasuredOnnxElementType.Int64, "batch_size", "decoder_sequence_length"),
                new MeasuredOnnxTensor("encoder_hidden_states", MeasuredOnnxElementType.Float32, "batch_size", "encoder_sequence_length", "512"),
            },
            outputs: new[]
            {
                new MeasuredOnnxTensor("logits", MeasuredOnnxElementType.Float32, "batch_size", "decoder_sequence_length", "46276"),
            });

        public static string BuildReport()
        {
            return
                "opus-mt measured ONNX" +
                " revision=" + LocalTranslationStagingContract.ExpectedRevision +
                " encoder=" + Encoder.FileName +
                " decoder=" + Decoder.FileName +
                " bytes=" + ReferenceRuntimeSizeBytes +
                " hidden=" + HiddenSize +
                " vocab=" + VocabularySize +
                " opset=" + Encoder.Opset;
        }
    }
}
