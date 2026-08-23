using System;
using System.Collections.Generic;
using System.Linq;

namespace PhraseLayer.Core.Translation
{
    /// <summary>
    /// Human-review dimensions for a local translation candidate.
    ///
    /// These dimensions are deliberately semantic/product-oriented rather than automatic metric names.
    /// PhraseLayer does not infer translation adequacy from BLEU, token parity, or runtime success.
    /// </summary>
    public enum TranslationQualityDimension
    {
        Adequacy = 0,
        NegationPolarity = 1,
        NamedEntity = 2,
        MultiwordExpression = 3,
        Modality = 4,
        TemporalAspect = 5,
        Quantity = 6,
        JapaneseReadability = 7,
    }

    public enum TranslationQualitySeverity
    {
        None = 0,
        Minor = 1,
        Major = 2,
        Critical = 3,
    }

    public sealed class TranslationQualityCase
    {
        public TranslationQualityCase(
            string id,
            string sourceText,
            IReadOnlyList<TranslationQualityDimension> dimensions,
            string rationale)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Quality case id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(sourceText)) throw new ArgumentException("Quality case source text is required.", nameof(sourceText));
            if (dimensions == null) throw new ArgumentNullException(nameof(dimensions));
            if (dimensions.Count == 0) throw new ArgumentException("Quality case must cover at least one dimension.", nameof(dimensions));
            if (string.IsNullOrWhiteSpace(rationale)) throw new ArgumentException("Quality case rationale is required.", nameof(rationale));

            var copiedDimensions = new TranslationQualityDimension[dimensions.Count];
            var seen = new HashSet<TranslationQualityDimension>();
            for (var index = 0; index < dimensions.Count; index++)
            {
                var dimension = dimensions[index];
                if (!seen.Add(dimension))
                    throw new ArgumentException("Quality case contains a duplicate dimension: " + dimension + ".", nameof(dimensions));
                copiedDimensions[index] = dimension;
            }

            Id = id.Trim();
            SourceText = sourceText;
            Dimensions = copiedDimensions;
            Rationale = rationale.Trim();
        }

        public string Id { get; }
        public string SourceText { get; }
        public IReadOnlyList<TranslationQualityDimension> Dimensions { get; }
        public string Rationale { get; }
    }

    /// <summary>
    /// Structured human assessment for one corpus case.
    ///
    /// Severity is supplied by the reviewer. The runtime never promotes a model because this class exists;
    /// it only makes the review evidence explicit, complete, and machine-checkable.
    /// </summary>
    public sealed class TranslationQualityReview
    {
        public TranslationQualityReview(
            string caseId,
            string candidateText,
            TranslationQualitySeverity severity,
            IReadOnlyList<TranslationQualityDimension> failedDimensions,
            string notes)
        {
            if (string.IsNullOrWhiteSpace(caseId)) throw new ArgumentException("Quality review case id is required.", nameof(caseId));
            if (candidateText == null) throw new ArgumentNullException(nameof(candidateText));
            if (failedDimensions == null) throw new ArgumentNullException(nameof(failedDimensions));
            if (notes == null) throw new ArgumentNullException(nameof(notes));

            var copiedFailures = new TranslationQualityDimension[failedDimensions.Count];
            var seen = new HashSet<TranslationQualityDimension>();
            for (var index = 0; index < failedDimensions.Count; index++)
            {
                var dimension = failedDimensions[index];
                if (!seen.Add(dimension))
                    throw new ArgumentException("Quality review contains a duplicate failed dimension: " + dimension + ".", nameof(failedDimensions));
                copiedFailures[index] = dimension;
            }

            if (severity == TranslationQualitySeverity.None && copiedFailures.Length != 0)
                throw new ArgumentException("A passing review cannot contain failed dimensions.", nameof(failedDimensions));
            if (severity != TranslationQualitySeverity.None && copiedFailures.Length == 0)
                throw new ArgumentException("A failing review must identify at least one failed dimension.", nameof(failedDimensions));

            CaseId = caseId.Trim();
            CandidateText = candidateText;
            Severity = severity;
            FailedDimensions = copiedFailures;
            Notes = notes.Trim();
        }

        public string CaseId { get; }
        public string CandidateText { get; }
        public TranslationQualitySeverity Severity { get; }
        public IReadOnlyList<TranslationQualityDimension> FailedDimensions { get; }
        public string Notes { get; }
    }

    public sealed class TranslationQualityPolicy
    {
        public TranslationQualityPolicy(
            int maxCriticalFailures,
            double maxMajorOrWorseRate,
            bool requireCompleteReview)
        {
            if (maxCriticalFailures < 0) throw new ArgumentOutOfRangeException(nameof(maxCriticalFailures));
            if (double.IsNaN(maxMajorOrWorseRate) || double.IsInfinity(maxMajorOrWorseRate) ||
                maxMajorOrWorseRate < 0.0 || maxMajorOrWorseRate > 1.0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxMajorOrWorseRate));
            }

            MaxCriticalFailures = maxCriticalFailures;
            MaxMajorOrWorseRate = maxMajorOrWorseRate;
            RequireCompleteReview = requireCompleteReview;
        }

        /// <summary>
        /// Candidate-promotion baseline: every corpus case must be reviewed, no critical semantic failures are
        /// accepted, and at most five percent of reviewed cases may remain major-or-worse.
        /// </summary>
        public static TranslationQualityPolicy CandidatePromotion { get; } =
            new TranslationQualityPolicy(0, 0.05, true);

        public int MaxCriticalFailures { get; }
        public double MaxMajorOrWorseRate { get; }
        public bool RequireCompleteReview { get; }
    }

    public sealed class TranslationQualitySummary
    {
        internal TranslationQualitySummary(
            int totalCases,
            int reviewedCases,
            int criticalFailures,
            int majorFailures,
            int minorFailures,
            IReadOnlyDictionary<TranslationQualityDimension, int> failedDimensionCounts,
            IReadOnlyList<string> missingCaseIds,
            double majorOrWorseRate,
            bool passed,
            string diagnostic)
        {
            TotalCases = totalCases;
            ReviewedCases = reviewedCases;
            CriticalFailures = criticalFailures;
            MajorFailures = majorFailures;
            MinorFailures = minorFailures;
            FailedDimensionCounts = failedDimensionCounts;
            MissingCaseIds = missingCaseIds;
            MajorOrWorseRate = majorOrWorseRate;
            Passed = passed;
            Diagnostic = diagnostic;
        }

        public int TotalCases { get; }
        public int ReviewedCases { get; }
        public int CriticalFailures { get; }
        public int MajorFailures { get; }
        public int MinorFailures { get; }
        public IReadOnlyDictionary<TranslationQualityDimension, int> FailedDimensionCounts { get; }
        public IReadOnlyList<string> MissingCaseIds { get; }
        public double MajorOrWorseRate { get; }
        public bool Passed { get; }
        public string Diagnostic { get; }
    }

    public static class TranslationQualityGate
    {
        public static TranslationQualitySummary Evaluate(
            IReadOnlyList<TranslationQualityCase> cases,
            IReadOnlyList<TranslationQualityReview> reviews,
            TranslationQualityPolicy policy)
        {
            if (cases == null) throw new ArgumentNullException(nameof(cases));
            if (reviews == null) throw new ArgumentNullException(nameof(reviews));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (cases.Count == 0) throw new ArgumentException("Translation quality corpus is empty.", nameof(cases));

            var casesById = new Dictionary<string, TranslationQualityCase>(StringComparer.Ordinal);
            for (var index = 0; index < cases.Count; index++)
            {
                var item = cases[index] ?? throw new ArgumentException("Translation quality corpus contains a null case.", nameof(cases));
                if (!casesById.TryAdd(item.Id, item))
                    throw new ArgumentException("Duplicate translation quality case id: " + item.Id + ".", nameof(cases));
            }

            var reviewsByCase = new Dictionary<string, TranslationQualityReview>(StringComparer.Ordinal);
            var failureCounts = new Dictionary<TranslationQualityDimension, int>();
            var critical = 0;
            var major = 0;
            var minor = 0;

            for (var index = 0; index < reviews.Count; index++)
            {
                var review = reviews[index] ?? throw new ArgumentException("Translation quality reviews contain a null review.", nameof(reviews));
                if (!casesById.ContainsKey(review.CaseId))
                    throw new ArgumentException("Translation quality review references unknown case: " + review.CaseId + ".", nameof(reviews));
                if (!reviewsByCase.TryAdd(review.CaseId, review))
                    throw new ArgumentException("Duplicate translation quality review for case: " + review.CaseId + ".", nameof(reviews));

                switch (review.Severity)
                {
                    case TranslationQualitySeverity.Critical:
                        critical++;
                        break;
                    case TranslationQualitySeverity.Major:
                        major++;
                        break;
                    case TranslationQualitySeverity.Minor:
                        minor++;
                        break;
                }

                for (var failureIndex = 0; failureIndex < review.FailedDimensions.Count; failureIndex++)
                {
                    var dimension = review.FailedDimensions[failureIndex];
                    if (!failureCounts.TryGetValue(dimension, out var count)) count = 0;
                    failureCounts[dimension] = count + 1;
                }
            }

            var missingCaseIds = casesById.Keys
                .Where(id => !reviewsByCase.ContainsKey(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            var reviewedCount = reviewsByCase.Count;
            var majorOrWorse = major + critical;
            var majorOrWorseRate = reviewedCount == 0 ? 0.0 : majorOrWorse / (double)reviewedCount;
            var complete = missingCaseIds.Length == 0;
            var passed =
                (!policy.RequireCompleteReview || complete) &&
                critical <= policy.MaxCriticalFailures &&
                majorOrWorseRate <= policy.MaxMajorOrWorseRate;

            var diagnostic =
                "translation-quality total=" + cases.Count +
                " reviewed=" + reviewedCount +
                " missing=" + missingCaseIds.Length +
                " critical=" + critical +
                " major=" + major +
                " minor=" + minor +
                " major_or_worse_rate=" + majorOrWorseRate.ToString("0.000") +
                " pass=" + (passed ? "true" : "false");

            return new TranslationQualitySummary(
                cases.Count,
                reviewedCount,
                critical,
                major,
                minor,
                new Dictionary<TranslationQualityDimension, int>(failureCounts),
                missingCaseIds,
                majorOrWorseRate,
                passed,
                diagnostic);
        }
    }
}
