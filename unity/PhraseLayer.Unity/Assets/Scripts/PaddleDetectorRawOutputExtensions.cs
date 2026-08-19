using System;
using System.Collections.Generic;
using PhraseLayer.Core.Inputs;

namespace PhraseLayer.Unity
{
    public static class PaddleDetectorRawOutputExtensions
    {
        /// <summary>
        /// Decodes a pinned PP-OCRv6 tiny detector output into source-frame image quads.
        /// The output tensor layout is accepted only when it proves a single DB map.
        /// PaddleOCR scales bitmap coordinates directly to the original src_w/src_h;
        /// the resize transform therefore supplies destination dimensions rather than an extra ratio transform.
        /// </summary>
        public static IReadOnlyList<PaddleDbQuadDetection> DecodeV6TinyQuads(
            this PaddleDetectorRawOutput output,
            PaddleDbPostprocessSpec spec = null)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));

            var map = PaddleDbProbabilityMap.FromTensor(output.OutputShape, output.OutputValues);
            var postprocessor = new PaddleDbQuadPostprocessor(spec ?? PaddleDbPostprocessSpec.V6Tiny());
            return postprocessor.Process(
                map,
                output.ResizeTransform.SourceWidth,
                output.ResizeTransform.SourceHeight);
        }
    }
}
