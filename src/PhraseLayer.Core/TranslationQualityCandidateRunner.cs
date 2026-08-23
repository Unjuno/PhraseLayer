using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PhraseLayer.Core.Translation
{
    public sealed class TranslationQualityCandidate
    {
        public TranslationQualityCandidate(string caseId, string sourceText, string candidateText)
        {
            if (string.IsNullOrWhiteSpace(caseId)) throw new ArgumentException("Quality candidate case id is required.", nameof(caseId));
            if (sourceText == null) throw new ArgumentNullException(nameof(sourceText));
            if (candidateText == null) throw new ArgumentNullException(nameof(candidateText));

            CaseId = caseId.Trim();
            SourceText = sourceText;
            CandidateText = candidateText;
        }

        public string CaseId { get; }
        public string SourceText { get; }
        public string CandidateText { get; }
    }

    /// <summary>
    /// Runs a fixed quality corpus through an ITranslationEngine without assigning any quality score.
    ///
    /// Execution is intentionally sequential. Unity Inference Engine workers are commonly main-thread-bound and
    /// are not assumed to be re-entrant. The runner therefore preserves corpus order and invokes exactly one
    /// translation at a time. Human review remains a separate step handled by TranslationQualityGate.
    /// </summary>
    public static class TranslationQualityCandidateRunner
    {
        public static async Task<IReadOnlyList<TranslationQualityCandidate>> RunAsync(
            IReadOnlyList<TranslationQualityCase> cases,
            ITranslationEngine translationEngine,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (cases == null) throw new ArgumentNullException(nameof(cases));
            if (translationEngine == null) throw new ArgumentNullException(nameof(translationEngine));
            if (cases.Count == 0) throw new ArgumentException("Translation quality corpus is empty.", nameof(cases));

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var output = new List<TranslationQualityCandidate>(cases.Count);
            for (var index = 0; index < cases.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var qualityCase = cases[index] ??
                    throw new ArgumentException("Translation quality corpus contains a null case.", nameof(cases));
                if (!ids.Add(qualityCase.Id))
                    throw new ArgumentException("Duplicate translation quality case id: " + qualityCase.Id + ".", nameof(cases));

                // The current corpus consists of self-contained sentences/signs. Passing the exact sentence as
                // context prevents accidental context-free behavior while keeping the evaluation input reproducible.
                var translated = await translationEngine
                    .TranslateAsync(qualityCase.SourceText, qualityCase.SourceText, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (translated == null)
                    throw new InvalidOperationException(
                        "Translation engine returned null for quality case '" + qualityCase.Id + "'.");

                output.Add(new TranslationQualityCandidate(
                    qualityCase.Id,
                    qualityCase.SourceText,
                    translated));
            }

            return output;
        }
    }
}
