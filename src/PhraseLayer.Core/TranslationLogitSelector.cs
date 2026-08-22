using System;
using System.Collections.Generic;

namespace PhraseLayer.Core.Translation
{
    /// <summary>
    /// Numerically stable logits-to-top-k-log-probability conversion shared by local translation backends.
    /// Keeping this in Core makes beam-search scoring independently testable from Unity/ONNX execution.
    /// </summary>
    public static class TranslationLogitSelector
    {
        public static IReadOnlyList<TranslationTokenCandidate> SelectTopLogProbabilities(
            IReadOnlyList<float> logits,
            int offset,
            int vocabularySize,
            int maxCandidates)
        {
            if (logits == null) throw new ArgumentNullException(nameof(logits));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (vocabularySize <= 0) throw new ArgumentOutOfRangeException(nameof(vocabularySize));
            if (maxCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
            if (offset > logits.Count - vocabularySize)
                throw new ArgumentOutOfRangeException(nameof(offset), "Requested vocabulary slice exceeds logits length.");

            var candidateLimit = Math.Min(vocabularySize, maxCandidates);
            var topIds = new int[candidateLimit];
            var topLogits = new double[candidateLimit];
            var topCount = 0;
            var maximum = double.NegativeInfinity;

            for (var tokenId = 0; tokenId < vocabularySize; tokenId++)
            {
                var value = (double)logits[offset + tokenId];
                if (double.IsNaN(value) || double.IsInfinity(value))
                    throw new InvalidOperationException("Translation decoder produced a non-finite logit at token " + tokenId + ".");
                if (value > maximum) maximum = value;

                var insertion = FindInsertion(topIds, topLogits, topCount, value, tokenId);
                if (insertion >= candidateLimit) continue;

                var last = Math.Min(topCount, candidateLimit - 1);
                for (var index = last; index > insertion; index--)
                {
                    topIds[index] = topIds[index - 1];
                    topLogits[index] = topLogits[index - 1];
                }
                topIds[insertion] = tokenId;
                topLogits[insertion] = value;
                if (topCount < candidateLimit) topCount++;
            }

            if (topCount == 0 || double.IsNegativeInfinity(maximum))
                throw new InvalidOperationException("Translation decoder produced no logits.");

            var exponentialSum = 0.0;
            for (var tokenId = 0; tokenId < vocabularySize; tokenId++)
                exponentialSum += Math.Exp((double)logits[offset + tokenId] - maximum);
            if (double.IsNaN(exponentialSum) || double.IsInfinity(exponentialSum) || exponentialSum <= 0.0)
                throw new InvalidOperationException("Translation decoder log-sum-exp normalization failed.");

            var logNormalizer = maximum + Math.Log(exponentialSum);
            var result = new List<TranslationTokenCandidate>(topCount);
            for (var index = 0; index < topCount; index++)
                result.Add(new TranslationTokenCandidate(topIds[index], topLogits[index] - logNormalizer));
            return result;
        }

        private static int FindInsertion(
            int[] topIds,
            double[] topLogits,
            int topCount,
            double value,
            int tokenId)
        {
            for (var index = 0; index < topCount; index++)
            {
                if (value > topLogits[index]) return index;
                if (value == topLogits[index] && tokenId < topIds[index]) return index;
            }
            return topCount;
        }
    }
}
