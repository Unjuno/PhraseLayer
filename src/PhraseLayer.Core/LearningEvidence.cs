using System;
using System.Collections.Generic;
using PhraseLayer.Core.Semantics;

namespace PhraseLayer.Core.Learning
{
    /// <summary>
    /// One observable or explicit signal tied to a semantic unit.
    /// The canonical LearningEvidenceKind and LearnerAdaptationEngine live in LearnerAdaptation.cs;
    /// this type adds reliability plus encounter-level deduplication without creating a second score engine.
    /// </summary>
    public sealed class LearningEvidence
    {
        public LearningEvidence(
            SemanticUnit unit,
            LearningEvidenceKind kind,
            double reliability = 1.0)
        {
            Unit = unit ?? throw new ArgumentNullException(nameof(unit));
            if (double.IsNaN(reliability) || double.IsInfinity(reliability) || reliability < 0.0 || reliability > 1.0)
                throw new ArgumentOutOfRangeException(nameof(reliability), "Evidence reliability must be finite and within [0,1].");
            Kind = kind;
            Reliability = reliability;
        }

        public SemanticUnit Unit { get; }
        public LearningEvidenceKind Kind { get; }
        public double Reliability { get; }
    }

    /// <summary>
    /// Prevents repeated camera/ASR frames from applying the same signal over and over during one encounter.
    /// Event producers should use a stable encounter id while a physical passage/conversation turn is being viewed.
    /// </summary>
    public sealed class LearningEncounterEvidenceTracker
    {
        private readonly LearnerAdaptationEngine adaptation;
        private readonly HashSet<string> applied = new HashSet<string>(StringComparer.Ordinal);

        public LearningEncounterEvidenceTracker(LearnerAdaptationEngine adaptation)
        {
            this.adaptation = adaptation ?? throw new ArgumentNullException(nameof(adaptation));
        }

        public LearnerUpdate? RecordOnce(string encounterId, LearningEvidence evidence)
        {
            if (string.IsNullOrWhiteSpace(encounterId))
                throw new ArgumentException("Encounter id is required.", nameof(encounterId));
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));

            var key = MakeKey(encounterId, evidence);
            if (!applied.Add(key))
                return null;
            return adaptation.Apply(evidence.Unit, evidence.Kind, evidence.Reliability);
        }

        public void EndEncounter(string encounterId)
        {
            if (string.IsNullOrWhiteSpace(encounterId))
                throw new ArgumentException("Encounter id is required.", nameof(encounterId));
            var prefix = encounterId + "\u001f";
            applied.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
        }

        public void Clear()
        {
            applied.Clear();
        }

        private static string MakeKey(string encounterId, LearningEvidence evidence)
        {
            return encounterId + "\u001f" + evidence.Unit.Id + "\u001f" + ((int)evidence.Kind).ToString();
        }
    }
}
