using System;
using System.Collections.Generic;
using System.Linq;
using PhraseLayer.Core.Semantics;

namespace PhraseLayer.Core.Learning
{
    /// <summary>
    /// Conservative encounter-level evidence collector.
    /// Repeated camera/ASR frames only refresh an observation; they do not immediately increase knowledge.
    /// Weak unassisted evidence is emitted only when the encounter ends and the unit did not require assistance,
    /// fail comprehension, or receive an explicit judgement during that encounter.
    /// </summary>
    public sealed class LearningEncounterRecorder
    {
        private readonly LearningEncounterEvidenceTracker tracker;
        private readonly Dictionary<string, EncounterState> encounters =
            new Dictionary<string, EncounterState>(StringComparer.Ordinal);

        public LearningEncounterRecorder(LearningEncounterEvidenceTracker tracker)
        {
            this.tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        }

        public int ActiveEncounterCount => encounters.Count;

        public void Observe(
            string encounterId,
            SemanticUnit unit,
            double reliability = 1.0)
        {
            ValidateEncounterId(encounterId);
            ValidateReliability(reliability);
            if (unit == null) throw new ArgumentNullException(nameof(unit));

            var state = GetOrCreateEncounter(encounterId);
            var unitState = state.GetOrCreate(unit);
            if (reliability > unitState.UnassistedReliability)
                unitState.UnassistedReliability = reliability;
        }

        public LearnerUpdate? AssistanceRequested(
            string encounterId,
            SemanticUnit unit,
            double reliability = 1.0)
        {
            return RecordBlockingEvidence(
                encounterId,
                unit,
                LearningEvidenceKind.AssistanceRequested,
                reliability);
        }

        public LearnerUpdate? AssistedSuccess(
            string encounterId,
            SemanticUnit unit,
            double reliability = 1.0)
        {
            return RecordBlockingEvidence(
                encounterId,
                unit,
                LearningEvidenceKind.AssistedExposure,
                reliability);
        }

        public LearnerUpdate? IncorrectComprehension(
            string encounterId,
            SemanticUnit unit,
            double reliability = 1.0)
        {
            return RecordBlockingEvidence(
                encounterId,
                unit,
                LearningEvidenceKind.ComprehensionFailed,
                reliability);
        }

        public LearnerUpdate? ExplicitKnown(
            string encounterId,
            SemanticUnit unit,
            double reliability = 1.0)
        {
            return RecordBlockingEvidence(
                encounterId,
                unit,
                LearningEvidenceKind.MarkedKnown,
                reliability);
        }

        public LearnerUpdate? ExplicitUnknown(
            string encounterId,
            SemanticUnit unit,
            double reliability = 1.0)
        {
            return RecordBlockingEvidence(
                encounterId,
                unit,
                LearningEvidenceKind.MarkedUnknown,
                reliability);
        }

        public IReadOnlyList<LearnerUpdate> EndEncounter(string encounterId)
        {
            ValidateEncounterId(encounterId);
            EncounterState state;
            if (!encounters.TryGetValue(encounterId, out state))
            {
                tracker.EndEncounter(encounterId);
                return Array.Empty<LearnerUpdate>();
            }

            var results = new List<LearnerUpdate>();
            foreach (var unitState in state.Units.Values
                         .OrderBy(item => item.Unit.Start)
                         .ThenByDescending(item => item.Unit.Length)
                         .ThenBy(item => item.Unit.Id, StringComparer.Ordinal))
            {
                if (unitState.BlocksUnassisted || unitState.UnassistedReliability <= 0.0)
                    continue;

                var result = tracker.RecordOnce(
                    encounterId,
                    new LearningEvidence(
                        unitState.Unit,
                        LearningEvidenceKind.CompletedWithoutAssistance,
                        unitState.UnassistedReliability));
                if (result != null)
                    results.Add(result);
            }

            encounters.Remove(encounterId);
            tracker.EndEncounter(encounterId);
            return results;
        }

        public void CancelEncounter(string encounterId)
        {
            ValidateEncounterId(encounterId);
            encounters.Remove(encounterId);
            tracker.EndEncounter(encounterId);
        }

        public void Clear()
        {
            encounters.Clear();
            tracker.Clear();
        }

        private LearnerUpdate? RecordBlockingEvidence(
            string encounterId,
            SemanticUnit unit,
            LearningEvidenceKind kind,
            double reliability)
        {
            ValidateEncounterId(encounterId);
            ValidateReliability(reliability);
            if (unit == null) throw new ArgumentNullException(nameof(unit));

            var state = GetOrCreateEncounter(encounterId);
            var unitState = state.GetOrCreate(unit);
            unitState.BlocksUnassisted = true;

            return tracker.RecordOnce(
                encounterId,
                new LearningEvidence(unit, kind, reliability));
        }

        private EncounterState GetOrCreateEncounter(string encounterId)
        {
            EncounterState state;
            if (!encounters.TryGetValue(encounterId, out state))
            {
                state = new EncounterState();
                encounters.Add(encounterId, state);
            }
            return state;
        }

        private static void ValidateEncounterId(string encounterId)
        {
            if (string.IsNullOrWhiteSpace(encounterId))
                throw new ArgumentException("Encounter id is required.", nameof(encounterId));
        }

        private static void ValidateReliability(double reliability)
        {
            if (double.IsNaN(reliability) ||
                double.IsInfinity(reliability) ||
                reliability < 0.0 ||
                reliability > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(reliability),
                    "Evidence reliability must be finite and within [0,1].");
            }
        }

        private sealed class EncounterState
        {
            private readonly Dictionary<string, UnitState> units =
                new Dictionary<string, UnitState>(StringComparer.Ordinal);

            public IReadOnlyDictionary<string, UnitState> Units => units;

            public UnitState GetOrCreate(SemanticUnit unit)
            {
                UnitState state;
                if (!units.TryGetValue(unit.Id, out state))
                {
                    state = new UnitState(unit);
                    units.Add(unit.Id, state);
                }
                return state;
            }
        }

        private sealed class UnitState
        {
            public UnitState(SemanticUnit unit)
            {
                Unit = unit;
            }

            public SemanticUnit Unit { get; }
            public double UnassistedReliability { get; set; }
            public bool BlocksUnassisted { get; set; }
        }
    }
}
