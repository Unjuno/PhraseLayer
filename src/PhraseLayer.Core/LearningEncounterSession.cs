using System;
using System.Collections.Generic;
using System.Linq;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;

namespace PhraseLayer.Core.Learning
{
    public sealed class LearningEncounterSummary
    {
        public LearningEncounterSummary(
            IReadOnlyList<LearnerUpdate> updates,
            bool successfulUnassistedCompletion)
        {
            Updates = updates ?? throw new ArgumentNullException(nameof(updates));
            SuccessfulUnassistedCompletion = successfulUnassistedCompletion;
        }

        public IReadOnlyList<LearnerUpdate> Updates { get; }

        /// <summary>
        /// Encounter/UI metadata only. This flag is deliberately not converted into learner evidence.
        /// Callers that have a genuinely verified unaided success must record VerifiedUnaidedSuccess
        /// for the specific semantic unit(s) that were actually verified.
        /// </summary>
        public bool SuccessfulUnassistedCompletion { get; }
    }

    /// <summary>
    /// Collects explicit/action-aware learner observations while a single mixed-language plan remains
    /// visually frozen. Observations are applied only when Finish is called, so the currently visible
    /// encounter never changes underneath the learner.
    ///
    /// The session deliberately does NOT synthesize learning evidence from passive assisted exposure,
    /// lack of a help request, or a generic "completed" flag. Those events are observationally censored
    /// unless engagement/success was independently verified.
    /// </summary>
    public sealed class LearningEncounterSession
    {
        private readonly MixedLanguagePlan plan;
        private readonly SemanticDocument document;
        private readonly LearnerAdaptationEngine adaptation;
        private readonly Dictionary<string, LearningObservation> pending =
            new Dictionary<string, LearningObservation>(StringComparer.Ordinal);
        private LearningEncounterSummary? finishedSummary;

        public LearningEncounterSession(MixedLanguagePlan plan, LearnerAdaptationEngine adaptation)
        {
            this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
            this.adaptation = adaptation ?? throw new ArgumentNullException(nameof(adaptation));
            document = plan.Document ?? throw new ArgumentException(
                "The mixed-language plan must retain its semantic document to create a learning encounter.",
                nameof(plan));
        }

        public MixedLanguagePlan Plan => plan;
        public bool IsFinished => finishedSummary != null;

        public SemanticUnit ResolveUnitAt(int sourceIndex)
        {
            if (sourceIndex < 0 || sourceIndex >= plan.SourceText.Length)
                throw new ArgumentOutOfRangeException(nameof(sourceIndex));

            var assisted = plan.Assistance.Decisions
                .Select(item => item.Unit)
                .Where(unit => ContainsIndex(unit, sourceIndex))
                .OrderBy(unit => unit.Length)
                .FirstOrDefault();
            if (assisted != null) return assisted;

            var containing = document.Units.Where(unit => ContainsIndex(unit, sourceIndex)).ToArray();
            var resolved = containing
                .OrderBy(unit => ResolutionPriority(unit.Kind))
                .ThenBy(unit => unit.Length)
                .FirstOrDefault();
            if (resolved == null)
                throw new InvalidOperationException("No semantic unit covers the requested source index.");
            return resolved;
        }

        public void RecordAt(int sourceIndex, LearningEvidenceKind evidence)
        {
            Record(ResolveUnitAt(sourceIndex), evidence);
        }

        public void Record(SemanticUnit unit, LearningEvidenceKind evidence)
        {
            EnsureOpen();
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            var canonical = FindCanonical(unit);
            var observation = LearningObservation.ForEvidence(
                canonical,
                evidence,
                assistedDisplay: IsAssisted(canonical));
            ValidateObservationAgainstEncounter(observation);
            pending[canonical.Id] = observation;
        }

        public void Record(
            SemanticUnit unit,
            LearningEvidenceKind evidence,
            LearningObservationOrigin origin,
            bool engagementVerified)
        {
            EnsureOpen();
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            var canonical = FindCanonical(unit);
            var observation = new LearningObservation(
                canonical,
                evidence,
                origin,
                engagementVerified);
            ValidateObservationAgainstEncounter(observation);
            pending[canonical.Id] = observation;
        }

        /// <summary>
        /// Records a positive source-only observation only when some external interaction/probe has
        /// genuinely verified that the learner processed this specific unassisted semantic unit.
        /// </summary>
        public void RecordVerifiedUnaidedSuccess(SemanticUnit unit)
        {
            EnsureOpen();
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            var canonical = FindCanonical(unit);
            if (IsAssisted(canonical))
            {
                throw new InvalidOperationException(
                    "Cannot record verified unaided success for a semantic unit that was assisted in this encounter.");
            }

            pending[canonical.Id] = new LearningObservation(
                canonical,
                LearningEvidenceKind.VerifiedUnaidedSuccess,
                LearningObservationOrigin.SourceDisplay,
                engagementVerified: true);
        }

        public void RecordVerifiedUnaidedSuccessAt(int sourceIndex)
        {
            RecordVerifiedUnaidedSuccess(ResolveUnitAt(sourceIndex));
        }

        /// <summary>
        /// Finalizes the encounter and applies only explicitly recorded observations exactly once.
        /// successfulUnassistedCompletion is retained as encounter metadata for compatibility, but it
        /// does not manufacture evidence for every untranslated token.
        /// </summary>
        public LearningEncounterSummary Finish(bool successfulUnassistedCompletion = false)
        {
            if (finishedSummary != null) return finishedSummary;

            var updates = new List<LearnerUpdate>(pending.Count);
            foreach (var observation in pending.Values
                         .OrderBy(item => item.Unit.Start)
                         .ThenBy(item => ResolutionPriority(item.Unit.Kind))
                         .ThenBy(item => item.Unit.Length))
            {
                var update = adaptation.Apply(observation);
                if (update.Applied) updates.Add(update);
            }

            finishedSummary = new LearningEncounterSummary(updates, successfulUnassistedCompletion);
            return finishedSummary;
        }

        private SemanticUnit FindCanonical(SemanticUnit candidate)
        {
            var unit = document.Units.FirstOrDefault(item =>
                string.Equals(item.Id, candidate.Id, StringComparison.Ordinal) &&
                item.Kind == candidate.Kind &&
                item.Start == candidate.Start &&
                item.Length == candidate.Length &&
                string.Equals(item.Text, candidate.Text, StringComparison.Ordinal));
            if (unit == null)
                throw new ArgumentException("Semantic unit does not belong to this encounter.", nameof(candidate));
            return unit;
        }

        private bool IsAssisted(SemanticUnit unit) =>
            plan.Assistance.Decisions.Any(item => item.Unit.Overlaps(unit));

        private void ValidateObservationAgainstEncounter(LearningObservation observation)
        {
            var assisted = IsAssisted(observation.Unit);
            switch (observation.Evidence)
            {
                case LearningEvidenceKind.AssistedExposure:
                    if (!assisted || observation.Origin != LearningObservationOrigin.AssistedDisplay)
                    {
                        throw new InvalidOperationException(
                            "Assisted exposure must originate from a semantic unit actually assisted in this encounter.");
                    }
                    break;

                case LearningEvidenceKind.CompletedWithoutAssistance:
                case LearningEvidenceKind.VerifiedUnaidedSuccess:
                    if (assisted || observation.Origin != LearningObservationOrigin.SourceDisplay)
                    {
                        throw new InvalidOperationException(
                            "Unaided source evidence cannot be recorded for a semantic unit assisted in this encounter.");
                    }
                    break;

                case LearningEvidenceKind.AssistanceRequested:
                    var expectedOrigin = assisted
                        ? LearningObservationOrigin.AssistedDisplay
                        : LearningObservationOrigin.SourceDisplay;
                    if (observation.Origin != expectedOrigin)
                    {
                        throw new InvalidOperationException(
                            "Assistance request origin does not match the display action for this encounter.");
                    }
                    break;

                case LearningEvidenceKind.RecallSucceeded:
                case LearningEvidenceKind.RecallFailed:
                    if (observation.Origin != LearningObservationOrigin.RecallProbe)
                        throw new InvalidOperationException("Recall evidence must originate from a recall probe.");
                    break;

                case LearningEvidenceKind.MarkedKnown:
                case LearningEvidenceKind.MarkedUnknown:
                    if (observation.Origin != LearningObservationOrigin.ExplicitSelfReport)
                        throw new InvalidOperationException("Known/unknown labels must originate from explicit self-report.");
                    break;
            }
        }

        private void EnsureOpen()
        {
            if (finishedSummary != null)
                throw new InvalidOperationException("The learning encounter has already been finished.");
        }

        private static bool ContainsIndex(SemanticUnit unit, int sourceIndex) =>
            unit.Start <= sourceIndex && sourceIndex < unit.End;

        private static int ResolutionPriority(SemanticUnitKind kind)
        {
            switch (kind)
            {
                case SemanticUnitKind.MultiwordExpression: return 0;
                case SemanticUnitKind.Word: return 1;
                case SemanticUnitKind.Phrase: return 2;
                case SemanticUnitKind.Clause: return 3;
                case SemanticUnitKind.Sentence: return 4;
                default: return 5;
            }
        }
    }
}
