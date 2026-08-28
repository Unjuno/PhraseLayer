using System;
using System.Collections.Generic;
using System.Linq;

namespace PhraseLayer.Core.Translation
{
    /// <summary>
    /// Narrows the broader graph-compatibility contract to the exact inputs the current correctness-first
    /// PhraseLayer backend knows how to populate. Graph inspection may accept additional exporter inputs so drift
    /// can be reported, but execution must never silently schedule a model with an unbound required input.
    /// </summary>
    public static class OpusMtEnJaMarianOnnxExecutionContract
    {
        public static void ValidateSupportedInputs(MarianOnnxBundleContractReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));

            RequireExactInputs(
                report.Encoder,
                new[]
                {
                    OpusMtEnJaMarianOnnxContract.EncoderInputIds,
                    OpusMtEnJaMarianOnnxContract.EncoderAttentionMask
                });

            RequireExactInputs(
                report.Decoder,
                new[]
                {
                    OpusMtEnJaMarianOnnxContract.DecoderInputIds,
                    OpusMtEnJaMarianOnnxContract.DecoderEncoderHiddenStates,
                    OpusMtEnJaMarianOnnxContract.DecoderEncoderAttentionMask
                });

            var withPastInputs = new List<string>
            {
                OpusMtEnJaMarianOnnxContract.DecoderInputIds,
                OpusMtEnJaMarianOnnxContract.DecoderEncoderHiddenStates,
                OpusMtEnJaMarianOnnxContract.DecoderEncoderAttentionMask
            };
            for (var layer = 0; layer < OpusMtEnJaMarianContract.ExpectedDecoderLayers; layer++)
            {
                withPastInputs.Add(OpusMtEnJaMarianOnnxContract.PastCacheName(layer, "decoder", "key"));
                withPastInputs.Add(OpusMtEnJaMarianOnnxContract.PastCacheName(layer, "decoder", "value"));
                withPastInputs.Add(OpusMtEnJaMarianOnnxContract.PastCacheName(layer, "encoder", "key"));
                withPastInputs.Add(OpusMtEnJaMarianOnnxContract.PastCacheName(layer, "encoder", "value"));
            }
            RequireExactInputs(report.DecoderWithPast, withPastInputs);
        }

        private static void RequireExactInputs(
            MarianOnnxGraphSignature graph,
            IEnumerable<string> supportedInputNames)
        {
            var supported = new HashSet<string>(supportedInputNames, StringComparer.Ordinal);
            var actual = new HashSet<string>(graph.Inputs.Select(input => input.Name), StringComparer.Ordinal);

            var missing = supported.Where(name => !actual.Contains(name)).OrderBy(name => name).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    "Marian Unity execution contract in '" + graph.GraphName +
                    "' is missing supported inputs already required by the graph contract: " +
                    string.Join(", ", missing) + ".");
            }

            var unsupported = actual.Where(name => !supported.Contains(name)).OrderBy(name => name).ToArray();
            if (unsupported.Length > 0)
            {
                throw new NotSupportedException(
                    "Marian Unity execution contract in '" + graph.GraphName +
                    "' contains required inputs that the current backend does not bind: " +
                    string.Join(", ", unsupported) +
                    ". Add an explicit binding and tests before executing this exporter revision.");
            }
        }
    }
}
