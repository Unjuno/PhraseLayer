using System;
using System.Collections.Generic;
using System.Linq;

namespace PhraseLayer.Core.Audio
{
    /// <summary>
    /// Runtime-neutral tensor metadata for the reviewed Moonshine v1 four-graph export.
    /// Names are retained for diagnostics, but the upstream ABI is positional: the reference
    /// runtime binds the first three decoder inputs explicitly and maps the remaining 24 cache
    /// states by input/output order.
    /// </summary>
    public enum MoonshineOnnxTensorElementType
    {
        Unknown,
        Integer,
        Float,
        Boolean
    }

    public sealed class MoonshineOnnxTensorSignature
    {
        public MoonshineOnnxTensorSignature(
            string name,
            MoonshineOnnxTensorElementType elementType = MoonshineOnnxTensorElementType.Unknown,
            int? rank = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tensor name must not be empty.", nameof(name));
            if (rank.HasValue && rank.Value < 0) throw new ArgumentOutOfRangeException(nameof(rank));
            Name = name;
            ElementType = elementType;
            Rank = rank;
        }

        public string Name { get; }
        public MoonshineOnnxTensorElementType ElementType { get; }
        public int? Rank { get; }
    }

    public sealed class MoonshineOnnxGraphSignature
    {
        public MoonshineOnnxGraphSignature(
            string graphName,
            IEnumerable<MoonshineOnnxTensorSignature> inputs,
            IEnumerable<MoonshineOnnxTensorSignature> outputs)
        {
            if (string.IsNullOrWhiteSpace(graphName)) throw new ArgumentException("Graph name must not be empty.", nameof(graphName));
            GraphName = graphName;
            Inputs = Copy(inputs, nameof(inputs));
            Outputs = Copy(outputs, nameof(outputs));
        }

        public string GraphName { get; }
        public IReadOnlyList<MoonshineOnnxTensorSignature> Inputs { get; }
        public IReadOnlyList<MoonshineOnnxTensorSignature> Outputs { get; }

        private static IReadOnlyList<MoonshineOnnxTensorSignature> Copy(
            IEnumerable<MoonshineOnnxTensorSignature> tensors,
            string parameterName)
        {
            if (tensors == null) throw new ArgumentNullException(parameterName);
            var result = tensors.ToArray();
            if (result.Any(item => item == null))
                throw new ArgumentException("Tensor signature collections cannot contain null entries.", parameterName);
            return result;
        }
    }

    public sealed class MoonshineOnnxBundleContractReport
    {
        internal MoonshineOnnxBundleContractReport(
            MoonshineOnnxGraphSignature preprocess,
            MoonshineOnnxGraphSignature encoder,
            MoonshineOnnxGraphSignature uncachedDecoder,
            MoonshineOnnxGraphSignature cachedDecoder)
        {
            Preprocess = preprocess;
            Encoder = encoder;
            UncachedDecoder = uncachedDecoder;
            CachedDecoder = cachedDecoder;
        }

        public MoonshineOnnxGraphSignature Preprocess { get; }
        public MoonshineOnnxGraphSignature Encoder { get; }
        public MoonshineOnnxGraphSignature UncachedDecoder { get; }
        public MoonshineOnnxGraphSignature CachedDecoder { get; }

        public override string ToString()
        {
            return string.Format(
                "Moonshine v1 ONNX bundle: preprocess={0}/{1}, encode={2}/{3}, uncached={4}/{5}, cached={6}/{7}, cache_states={8}",
                Preprocess.Inputs.Count,
                Preprocess.Outputs.Count,
                Encoder.Inputs.Count,
                Encoder.Outputs.Count,
                UncachedDecoder.Inputs.Count,
                UncachedDecoder.Outputs.Count,
                CachedDecoder.Inputs.Count,
                CachedDecoder.Outputs.Count,
                MoonshineTinyV1OnnxContract.CacheStateCount);
        }
    }

    /// <summary>
    /// Contract for the original English Moonshine v1 deployment split used by the upstream
    /// UsefulSensors export and sherpa-onnx reference runtime:
    /// preprocess.onnx, encode.onnx, uncached_decode.onnx, cached_decode.onnx.
    ///
    /// The reference runtime establishes the ABI by position rather than stable tensor names:
    /// preprocess receives waveform; encoder receives features + feature length; uncached decoder
    /// receives token + encoder output + token length and emits logits + 24 states; cached decoder
    /// receives the same three values + those 24 states and emits logits + 24 replacement states.
    /// Exact counts are therefore correctness requirements, not cosmetic validation.
    /// </summary>
    public static class MoonshineTinyV1OnnxContract
    {
        public const int CacheStateCount = 24;
        public const int UncachedDecoderInputCount = 3;
        public const int CachedDecoderInputCount = 3 + CacheStateCount;
        public const int DecoderOutputCount = 1 + CacheStateCount;

        public static MoonshineOnnxBundleContractReport ValidateBundle(
            MoonshineOnnxGraphSignature preprocess,
            MoonshineOnnxGraphSignature encoder,
            MoonshineOnnxGraphSignature uncachedDecoder,
            MoonshineOnnxGraphSignature cachedDecoder)
        {
            if (preprocess == null) throw new ArgumentNullException(nameof(preprocess));
            if (encoder == null) throw new ArgumentNullException(nameof(encoder));
            if (uncachedDecoder == null) throw new ArgumentNullException(nameof(uncachedDecoder));
            if (cachedDecoder == null) throw new ArgumentNullException(nameof(cachedDecoder));

            RequireCount(preprocess, inputs: 1, outputs: 1);
            Require(preprocess.Inputs[0], MoonshineOnnxTensorElementType.Float, 2, "preprocess waveform input");
            Require(preprocess.Outputs[0], MoonshineOnnxTensorElementType.Float, 3, "preprocess feature output");

            RequireCount(encoder, inputs: 2, outputs: 1);
            Require(encoder.Inputs[0], MoonshineOnnxTensorElementType.Float, 3, "encoder feature input");
            Require(encoder.Inputs[1], MoonshineOnnxTensorElementType.Integer, 1, "encoder feature-length input");
            Require(encoder.Outputs[0], MoonshineOnnxTensorElementType.Float, 3, "encoder hidden-state output");

            RequireCount(uncachedDecoder, inputs: UncachedDecoderInputCount, outputs: DecoderOutputCount);
            ValidateDecoderBaseInputs(uncachedDecoder, "uncached decoder");
            ValidateDecoderOutputs(uncachedDecoder, "uncached decoder");

            RequireCount(cachedDecoder, inputs: CachedDecoderInputCount, outputs: DecoderOutputCount);
            ValidateDecoderBaseInputs(cachedDecoder, "cached decoder");
            for (var index = 0; index < CacheStateCount; index++)
                Require(cachedDecoder.Inputs[3 + index], MoonshineOnnxTensorElementType.Float, 4, "cached decoder state input " + index);
            ValidateDecoderOutputs(cachedDecoder, "cached decoder");

            return new MoonshineOnnxBundleContractReport(preprocess, encoder, uncachedDecoder, cachedDecoder);
        }

        private static void ValidateDecoderBaseInputs(MoonshineOnnxGraphSignature graph, string label)
        {
            Require(graph.Inputs[0], MoonshineOnnxTensorElementType.Integer, 2, label + " token input");
            Require(graph.Inputs[1], MoonshineOnnxTensorElementType.Float, 3, label + " encoder-state input");
            Require(graph.Inputs[2], MoonshineOnnxTensorElementType.Integer, 1, label + " token-length input");
        }

        private static void ValidateDecoderOutputs(MoonshineOnnxGraphSignature graph, string label)
        {
            Require(graph.Outputs[0], MoonshineOnnxTensorElementType.Float, 3, label + " logits output");
            for (var index = 0; index < CacheStateCount; index++)
                Require(graph.Outputs[1 + index], MoonshineOnnxTensorElementType.Float, 4, label + " state output " + index);
        }

        private static void RequireCount(MoonshineOnnxGraphSignature graph, int inputs, int outputs)
        {
            if (graph.Inputs.Count != inputs || graph.Outputs.Count != outputs)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Moonshine graph {0} ABI drift: expected {1} inputs/{2} outputs but received {3}/{4}.",
                        graph.GraphName,
                        inputs,
                        outputs,
                        graph.Inputs.Count,
                        graph.Outputs.Count));
            }
        }

        private static void Require(
            MoonshineOnnxTensorSignature tensor,
            MoonshineOnnxTensorElementType expectedType,
            int expectedRank,
            string label)
        {
            if (tensor.ElementType != MoonshineOnnxTensorElementType.Unknown && tensor.ElementType != expectedType)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Moonshine {0} dtype drift at {1}: expected {2}, received {3}.",
                        label,
                        tensor.Name,
                        expectedType,
                        tensor.ElementType));
            }
            if (tensor.Rank.HasValue && tensor.Rank.Value != expectedRank)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Moonshine {0} rank drift at {1}: expected {2}, received {3}.",
                        label,
                        tensor.Name,
                        expectedRank,
                        tensor.Rank.Value));
            }
        }
    }
}
