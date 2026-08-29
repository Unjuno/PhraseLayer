using System;
using System.Collections.Generic;
using System.Linq;

namespace PhraseLayer.Core.Translation
{
    /// <summary>
    /// Runtime-neutral tensor categories used to validate an exported Marian ONNX bundle without taking a
    /// dependency on ONNX Runtime, Unity Inference Engine, or a concrete exporter package.
    /// </summary>
    public enum MarianOnnxTensorElementType
    {
        Unknown,
        Integer,
        Float,
        Boolean
    }

    public sealed class MarianOnnxTensorSignature
    {
        public MarianOnnxTensorSignature(
            string name,
            MarianOnnxTensorElementType elementType = MarianOnnxTensorElementType.Unknown,
            int? rank = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Tensor name must not be empty.", nameof(name));
            if (rank.HasValue && rank.Value < 0)
                throw new ArgumentOutOfRangeException(nameof(rank));

            Name = name;
            ElementType = elementType;
            Rank = rank;
        }

        public string Name { get; }
        public MarianOnnxTensorElementType ElementType { get; }
        public int? Rank { get; }
    }

    public sealed class MarianOnnxGraphSignature
    {
        private readonly IReadOnlyDictionary<string, MarianOnnxTensorSignature> inputsByName;
        private readonly IReadOnlyDictionary<string, MarianOnnxTensorSignature> outputsByName;

        public MarianOnnxGraphSignature(
            string graphName,
            IEnumerable<MarianOnnxTensorSignature> inputs,
            IEnumerable<MarianOnnxTensorSignature> outputs)
        {
            if (string.IsNullOrWhiteSpace(graphName))
                throw new ArgumentException("Graph name must not be empty.", nameof(graphName));
            GraphName = graphName;
            Inputs = CopyUnique(inputs, nameof(inputs));
            Outputs = CopyUnique(outputs, nameof(outputs));
            inputsByName = Inputs.ToDictionary(tensor => tensor.Name, StringComparer.Ordinal);
            outputsByName = Outputs.ToDictionary(tensor => tensor.Name, StringComparer.Ordinal);
        }

        public string GraphName { get; }
        public IReadOnlyList<MarianOnnxTensorSignature> Inputs { get; }
        public IReadOnlyList<MarianOnnxTensorSignature> Outputs { get; }

        public bool TryGetInput(string name, out MarianOnnxTensorSignature? tensor)
        {
            return inputsByName.TryGetValue(name, out tensor);
        }

        public bool TryGetOutput(string name, out MarianOnnxTensorSignature? tensor)
        {
            return outputsByName.TryGetValue(name, out tensor);
        }

        private static IReadOnlyList<MarianOnnxTensorSignature> CopyUnique(
            IEnumerable<MarianOnnxTensorSignature> tensors,
            string parameterName)
        {
            if (tensors == null) throw new ArgumentNullException(parameterName);
            var copied = tensors.ToArray();
            if (copied.Any(tensor => tensor == null))
                throw new ArgumentException("Tensor signature collections cannot contain null entries.", parameterName);

            var duplicate = copied
                .GroupBy(tensor => tensor.Name, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
                throw new ArgumentException("Duplicate tensor name: " + duplicate.Key, parameterName);
            return copied;
        }
    }

    public sealed class MarianOnnxBundleContractReport
    {
        internal MarianOnnxBundleContractReport(
            MarianOnnxGraphSignature encoder,
            MarianOnnxGraphSignature decoder,
            MarianOnnxGraphSignature decoderWithPast,
            bool decoderWithPastReturnsCrossAttentionCache)
        {
            Encoder = encoder;
            Decoder = decoder;
            DecoderWithPast = decoderWithPast;
            DecoderWithPastReturnsCrossAttentionCache = decoderWithPastReturnsCrossAttentionCache;
        }

        public MarianOnnxGraphSignature Encoder { get; }
        public MarianOnnxGraphSignature Decoder { get; }
        public MarianOnnxGraphSignature DecoderWithPast { get; }
        public bool DecoderWithPastReturnsCrossAttentionCache { get; }

        public override string ToString()
        {
            return string.Format(
                "Marian ONNX bundle: encoder={0}/{1}, decoder={2}/{3}, decoder_with_past={4}/{5}, layers={6}, cross-cache-output={7}",
                Encoder.Inputs.Count,
                Encoder.Outputs.Count,
                Decoder.Inputs.Count,
                Decoder.Outputs.Count,
                DecoderWithPast.Inputs.Count,
                DecoderWithPast.Outputs.Count,
                OpusMtEnJaMarianContract.ExpectedDecoderLayers,
                DecoderWithPastReturnsCrossAttentionCache);
        }
    }

    /// <summary>
    /// Strict graph-name/cache contract for the reviewed Optimum three-file seq2seq export:
    /// encoder_model.onnx, decoder_model.onnx, decoder_with_past_model.onnx.
    ///
    /// Extra exporter inputs/outputs are allowed so explicit additions such as cache_position do not create a
    /// false negative. Required semantic tensors and all six Marian cache layers must still exist. Unknown dtype
    /// or rank metadata is tolerated only when the host API does not expose it statically; when metadata is known,
    /// drift fails immediately.
    /// </summary>
    public static class OpusMtEnJaMarianOnnxContract
    {
        public const string EncoderInputIds = "input_ids";
        public const string EncoderAttentionMask = "attention_mask";
        public const string EncoderLastHiddenState = "last_hidden_state";
        public const string DecoderInputIds = "input_ids";
        public const string DecoderEncoderHiddenStates = "encoder_hidden_states";
        public const string DecoderEncoderAttentionMask = "encoder_attention_mask";
        public const string DecoderLogits = "logits";

        public static MarianOnnxBundleContractReport ValidateBundle(
            MarianOnnxGraphSignature encoder,
            MarianOnnxGraphSignature decoder,
            MarianOnnxGraphSignature decoderWithPast)
        {
            if (encoder == null) throw new ArgumentNullException(nameof(encoder));
            if (decoder == null) throw new ArgumentNullException(nameof(decoder));
            if (decoderWithPast == null) throw new ArgumentNullException(nameof(decoderWithPast));

            RequireInput(encoder, EncoderInputIds, MarianOnnxTensorElementType.Integer, 2);
            RequireInput(encoder, EncoderAttentionMask, MarianOnnxTensorElementType.Integer, 2);
            RequireOutput(encoder, EncoderLastHiddenState, MarianOnnxTensorElementType.Float, 3);

            RequireInput(decoder, DecoderInputIds, MarianOnnxTensorElementType.Integer, 2);
            RequireInput(decoder, DecoderEncoderHiddenStates, MarianOnnxTensorElementType.Float, 3);
            RequireInput(decoder, DecoderEncoderAttentionMask, MarianOnnxTensorElementType.Integer, 2);
            RequireOutput(decoder, DecoderLogits, MarianOnnxTensorElementType.Float, 3);
            RequirePresentCacheOutputs(decoder, requireCrossAttention: true);

            RequireInput(decoderWithPast, DecoderInputIds, MarianOnnxTensorElementType.Integer, 2);
            RequireInput(decoderWithPast, DecoderEncoderHiddenStates, MarianOnnxTensorElementType.Float, 3);
            RequireInput(decoderWithPast, DecoderEncoderAttentionMask, MarianOnnxTensorElementType.Integer, 2);
            RequireOutput(decoderWithPast, DecoderLogits, MarianOnnxTensorElementType.Float, 3);
            RequirePastCacheInputs(decoderWithPast);
            var returnsCrossAttentionCache = RequirePresentCacheOutputs(
                decoderWithPast,
                requireCrossAttention: false);

            RejectUnexpectedCacheLayers(decoder);
            RejectUnexpectedCacheLayers(decoderWithPast);

            return new MarianOnnxBundleContractReport(
                encoder,
                decoder,
                decoderWithPast,
                returnsCrossAttentionCache);
        }

        public static string PastCacheName(int layer, string attentionKind, string keyOrValue)
        {
            return string.Format("past_key_values.{0}.{1}.{2}", layer, attentionKind, keyOrValue);
        }

        public static string PresentCacheName(int layer, string attentionKind, string keyOrValue)
        {
            return string.Format("present.{0}.{1}.{2}", layer, attentionKind, keyOrValue);
        }

        private static void RequirePastCacheInputs(MarianOnnxGraphSignature graph)
        {
            for (var layer = 0; layer < OpusMtEnJaMarianContract.ExpectedDecoderLayers; layer++)
            {
                RequireInput(graph, PastCacheName(layer, "decoder", "key"), MarianOnnxTensorElementType.Float, 4);
                RequireInput(graph, PastCacheName(layer, "decoder", "value"), MarianOnnxTensorElementType.Float, 4);
                RequireInput(graph, PastCacheName(layer, "encoder", "key"), MarianOnnxTensorElementType.Float, 4);
                RequireInput(graph, PastCacheName(layer, "encoder", "value"), MarianOnnxTensorElementType.Float, 4);
            }
        }

        private static bool RequirePresentCacheOutputs(
            MarianOnnxGraphSignature graph,
            bool requireCrossAttention)
        {
            var anyCrossAttentionOutput = false;
            for (var layer = 0; layer < OpusMtEnJaMarianContract.ExpectedDecoderLayers; layer++)
            {
                RequireOutput(graph, PresentCacheName(layer, "decoder", "key"), MarianOnnxTensorElementType.Float, 4);
                RequireOutput(graph, PresentCacheName(layer, "decoder", "value"), MarianOnnxTensorElementType.Float, 4);

                var encoderKey = PresentCacheName(layer, "encoder", "key");
                var encoderValue = PresentCacheName(layer, "encoder", "value");
                var hasKey = graph.TryGetOutput(encoderKey, out _);
                var hasValue = graph.TryGetOutput(encoderValue, out _);
                if (hasKey != hasValue)
                {
                    throw Drift(graph, "cross-attention cache output pair is incomplete for layer " + layer + ".");
                }

                if (requireCrossAttention && !hasKey)
                {
                    throw Drift(graph, "missing required output '" + encoderKey + "'.");
                }

                if (hasKey)
                {
                    anyCrossAttentionOutput = true;
                    RequireOutput(graph, encoderKey, MarianOnnxTensorElementType.Float, 4);
                    RequireOutput(graph, encoderValue, MarianOnnxTensorElementType.Float, 4);
                }
            }

            if (anyCrossAttentionOutput)
            {
                for (var layer = 0; layer < OpusMtEnJaMarianContract.ExpectedDecoderLayers; layer++)
                {
                    if (!graph.TryGetOutput(PresentCacheName(layer, "encoder", "key"), out _) ||
                        !graph.TryGetOutput(PresentCacheName(layer, "encoder", "value"), out _))
                    {
                        throw Drift(graph, "cross-attention present-cache outputs must be all-or-none across decoder layers.");
                    }
                }
            }

            return anyCrossAttentionOutput;
        }

        private static void RejectUnexpectedCacheLayers(MarianOnnxGraphSignature graph)
        {
            foreach (var tensor in graph.Inputs.Concat(graph.Outputs))
            {
                var parts = tensor.Name.Split('.');
                if (parts.Length < 4) continue;
                if (!string.Equals(parts[0], "past_key_values", StringComparison.Ordinal) &&
                    !string.Equals(parts[0], "present", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!int.TryParse(parts[1], out var layer))
                    throw Drift(graph, "cache tensor has a non-numeric layer index: '" + tensor.Name + "'.");
                if (layer < 0 || layer >= OpusMtEnJaMarianContract.ExpectedDecoderLayers)
                {
                    throw Drift(
                        graph,
                        string.Format(
                            "cache tensor '{0}' references layer {1}, outside reviewed layer range 0..{2}.",
                            tensor.Name,
                            layer,
                            OpusMtEnJaMarianContract.ExpectedDecoderLayers - 1));
                }
            }
        }

        private static void RequireInput(
            MarianOnnxGraphSignature graph,
            string name,
            MarianOnnxTensorElementType elementType,
            int rank)
        {
            if (!graph.TryGetInput(name, out var tensor) || tensor == null)
                throw Drift(graph, "missing required input '" + name + "'.");
            ValidateTensor(graph, tensor, elementType, rank, "input");
        }

        private static void RequireOutput(
            MarianOnnxGraphSignature graph,
            string name,
            MarianOnnxTensorElementType elementType,
            int rank)
        {
            if (!graph.TryGetOutput(name, out var tensor) || tensor == null)
                throw Drift(graph, "missing required output '" + name + "'.");
            ValidateTensor(graph, tensor, elementType, rank, "output");
        }

        private static void ValidateTensor(
            MarianOnnxGraphSignature graph,
            MarianOnnxTensorSignature tensor,
            MarianOnnxTensorElementType expectedType,
            int expectedRank,
            string direction)
        {
            if (tensor.ElementType != MarianOnnxTensorElementType.Unknown && tensor.ElementType != expectedType)
            {
                throw Drift(
                    graph,
                    string.Format(
                        "{0} '{1}' type expected {2} but found {3}.",
                        direction,
                        tensor.Name,
                        expectedType,
                        tensor.ElementType));
            }

            if (tensor.Rank.HasValue && tensor.Rank.Value != expectedRank)
            {
                throw Drift(
                    graph,
                    string.Format(
                        "{0} '{1}' rank expected {2} but found {3}.",
                        direction,
                        tensor.Name,
                        expectedRank,
                        tensor.Rank.Value));
            }
        }

        private static InvalidOperationException Drift(MarianOnnxGraphSignature graph, string message)
        {
            return new InvalidOperationException(
                "OPUS-MT en-ja Marian ONNX graph drift in '" + graph.GraphName + "': " + message);
        }
    }
}
