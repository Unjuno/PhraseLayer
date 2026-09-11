using System;
using System.Collections.Generic;
using System.Linq;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;

namespace PhraseLayer.Core.Learning
{
    public sealed class LearningEncounterSummary
    {
        public LearningEncounterSummary(IReadOnlyList<LearnerUpdate> updates, bool successfulUnassistedCompletion)
        { Updates = updates ?? throw new ArgumentNullException(nameof(updates)); SuccessfulUnassistedCompletion = successfulUnassistedCompletion; }
        public IReadOnlyList<LearnerUpdate> Updates { get; }
        public bool SuccessfulUnassistedCompletion { get; }
    }
    /// <summary>
    /// Evidence is collected while the displayed plan stays frozen. Finish prepares a snapshot once, then
    /// commits it. A storage retry must reuse this session and the same completion flag; it never replays gains.
    /// Automatic evidence is limited to one update per normalized knowledge key per encounter.
    /// This is single-owner retry safety, not a process-crash durable event ledger.
    /// </summary>
    public sealed class LearningEncounterSession
    {
        private readonly MixedLanguagePlan plan;
        private readonly SemanticDocument document;
        private readonly LearnerAdaptationEngine adaptation;
        private readonly Dictionary<string, PendingEvidence> pending = new Dictionary<string, PendingEvidence>(StringComparer.Ordinal);
        private LearningEncounterSummary? finishedSummary;
        private LearnerUpdateBatch? preparedBatch;
        private bool preparedSuccessfulCompletion;
        public LearningEncounterSession(MixedLanguagePlan plan, LearnerAdaptationEngine adaptation)
        {
            this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
            this.adaptation = adaptation ?? throw new ArgumentNullException(nameof(adaptation));
            document = plan.Document ?? throw new ArgumentException("The mixed-language plan must retain its semantic document to create a learning encounter.", nameof(plan));
        }
        public MixedLanguagePlan Plan => plan;
        public bool IsFinished => finishedSummary != null;
        public bool IsCommitPending => preparedBatch != null && finishedSummary == null;
        public SemanticUnit ResolveUnitAt(int sourceIndex)
        {
            if (sourceIndex < 0 || sourceIndex >= plan.SourceText.Length) throw new ArgumentOutOfRangeException(nameof(sourceIndex));
            var assisted = plan.Assistance.Decisions.Select(item => item.Unit).Where(unit => ContainsIndex(unit, sourceIndex))
                .OrderBy(unit => unit.Length).FirstOrDefault();
            if (assisted != null) return assisted;
            var resolved = document.Units.Where(unit => ContainsIndex(unit, sourceIndex))
                .OrderBy(unit => ResolutionPriority(unit.Kind)).ThenBy(unit => unit.Length).FirstOrDefault();
            if (resolved == null) throw new InvalidOperationException("No semantic unit covers the requested source index.");
            return resolved;
        }
        public void RecordAt(int sourceIndex, LearningEvidenceKind evidence) { Record(ResolveUnitAt(sourceIndex), evidence); }
        public void Record(SemanticUnit unit, LearningEvidenceKind evidence)
        {
            EnsureOpen();
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            if (!Enum.IsDefined(typeof(LearningEvidenceKind), evidence))
                throw new ArgumentOutOfRangeException(nameof(evidence), evidence, "Unknown learning evidence kind.");
            var canonical = FindCanonical(unit);
            pending[canonical.Id] = new PendingEvidence(canonical, evidence);
        }
        public LearningEncounterSummary Finish(bool successfulUnassistedCompletion = false)
        {
            if (finishedSummary != null) return finishedSummary;
            if (preparedBatch == null)
            {
                var evidence = BuildEvidence(successfulUnassistedCompletion);
                preparedBatch = adaptation.PrepareBatch(evidence.Values.OrderBy(item => item.Unit.Start)
                    .ThenBy(item => ResolutionPriority(item.Unit.Kind)).ThenBy(item => item.Unit.Length)
                    .Select(item => new LearnerEvidence(item.Unit, item.Evidence)));
                preparedSuccessfulCompletion = successfulUnassistedCompletion;
            }
            else if (preparedSuccessfulCompletion != successfulUnassistedCompletion)
                throw new InvalidOperationException("A pending commit must be retried with its original completion flag.");
            preparedBatch.Commit();
            finishedSummary = new LearningEncounterSummary(preparedBatch.Updates, preparedSuccessfulCompletion);
            return finishedSummary;
        }
        private Dictionary<string, PendingEvidence> BuildEvidence(bool successfulUnassistedCompletion)
        {
            var evidence = new Dictionary<string, PendingEvidence>(pending, StringComparer.Ordinal);
            // Explicit evidence on any occurrence takes precedence over automatic evidence on the same knowledge key.
            var automaticKeys = new HashSet<string>(pending.Values.Select(item => InMemoryLearnerModel.Normalize(item.Unit.Text)), StringComparer.OrdinalIgnoreCase);
            foreach (var decision in plan.Assistance.Decisions)
            {
                if (!HasTranslatedDisplayFor(decision.Unit)) continue;
                AddAutomaticEvidence(evidence, automaticKeys, decision.Unit, LearningEvidenceKind.AssistedExposure);
            }
            if (successfulUnassistedCompletion)
            {
                // Keep planned assistance excluded even when translation failed: absence of a translation is not proof of unassisted success.
                var assistedUnits = plan.Assistance.Decisions.Select(item => item.Unit).ToArray();
                foreach (var atom in BuildAtomicUnits(document))
                {
                    if (assistedUnits.Any(unit => unit.Overlaps(atom))) continue;
                    AddAutomaticEvidence(evidence, automaticKeys, atom, LearningEvidenceKind.CompletedWithoutAssistance);
                }
            }
            return evidence;
        }
        private bool HasTranslatedDisplayFor(SemanticUnit unit)
        {
            var sourceKey = InMemoryLearnerModel.Normalize(unit.Text);
            return plan.Segments.Any(segment => segment.IsAssisted && segment.Unit != null &&
                segment.Unit.Id == unit.Id && segment.Unit.Start == unit.Start && segment.Unit.Length == unit.Length &&
                string.Equals(segment.SourceText, unit.Text, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(segment.DisplayText) &&
                !string.Equals(sourceKey, InMemoryLearnerModel.Normalize(segment.DisplayText), StringComparison.Ordinal));
        }
        private static void AddAutomaticEvidence(Dictionary<string, PendingEvidence> evidence, HashSet<string> keys,
            SemanticUnit unit, LearningEvidenceKind kind)
        {
            if (evidence.ContainsKey(unit.Id) || !keys.Add(InMemoryLearnerModel.Normalize(unit.Text))) return;
            evidence.Add(unit.Id, new PendingEvidence(unit, kind));
        }
        private SemanticUnit FindCanonical(SemanticUnit candidate)
        {
            var unit = document.Units.FirstOrDefault(item => string.Equals(item.Id, candidate.Id, StringComparison.Ordinal) &&
                item.Kind == candidate.Kind && item.Start == candidate.Start && item.Length == candidate.Length &&
                string.Equals(item.Text, candidate.Text, StringComparison.Ordinal));
            if (unit == null) throw new ArgumentException("Semantic unit does not belong to this encounter.", nameof(candidate));
            return unit;
        }
        private void EnsureOpen()
        {
            if (finishedSummary != null) throw new InvalidOperationException("The learning encounter has already been finished.");
            if (preparedBatch != null) throw new InvalidOperationException("Evidence is frozen while an encounter commit is pending.");
        }
        private static IEnumerable<SemanticUnit> BuildAtomicUnits(SemanticDocument document)
        {
            var mwes = document.OfKind(SemanticUnitKind.MultiwordExpression).OrderBy(unit => unit.Start).ToArray();
            foreach (var mwe in mwes) yield return mwe;
            foreach (var word in document.OfKind(SemanticUnitKind.Word)) if (!mwes.Any(mwe => mwe.Overlaps(word))) yield return word;
        }
        private static bool ContainsIndex(SemanticUnit unit, int sourceIndex) => unit.Start <= sourceIndex && sourceIndex < unit.End;
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
        private readonly struct PendingEvidence
        {
            public PendingEvidence(SemanticUnit unit, LearningEvidenceKind evidence) { Unit = unit; Evidence = evidence; }
            public SemanticUnit Unit { get; }
            public LearningEvidenceKind Evidence { get; }
        }
    }
}
