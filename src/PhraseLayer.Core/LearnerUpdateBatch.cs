using System;
using System.Collections.Generic;
using PhraseLayer.Core.Semantics;

namespace PhraseLayer.Core.Learning
{
    public sealed class LearnerEvidence
    {
        public LearnerEvidence(SemanticUnit unit, LearningEvidenceKind kind)
        {
            Unit = unit ?? throw new ArgumentNullException(nameof(unit));
            if (!Enum.IsDefined(typeof(LearningEvidenceKind), kind))
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown learning evidence kind.");
            Kind = kind;
        }
        public SemanticUnit Unit { get; }
        public LearningEvidenceKind Kind { get; }
    }

    /// <summary>
    /// A frozen before/after learner update. Retry replaces the same snapshot, never reapplies learning gains.
    /// Call from one serialized owner, as for the existing learner models. The state comparison is not a
    /// multithreaded compare-and-swap or a durable event ledger. Store.Save must replace a complete snapshot.
    /// The batch must survive any retry; process-crash/recreated-session exactly-once delivery is not claimed.
    /// </summary>
    public sealed class LearnerUpdateBatch
    {
        private readonly IMutableLearnerModel learner;
        private bool committed;
        internal LearnerUpdateBatch(IMutableLearnerModel learner, LearnerProfileSnapshot before,
            LearnerProfileSnapshot after, List<LearnerUpdate> updates)
        {
            this.learner = learner;
            BeforeSnapshot = before;
            AfterSnapshot = after;
            Updates = Array.AsReadOnly(updates.ToArray());
        }
        public LearnerProfileSnapshot BeforeSnapshot { get; }
        public LearnerProfileSnapshot AfterSnapshot { get; }
        public IReadOnlyList<LearnerUpdate> Updates { get; }
        public bool IsCommitted => committed;

        public void Commit()
        {
            if (committed) return;
            var current = learner.CreateSnapshot();
            if (!Equivalent(current, BeforeSnapshot) && !Equivalent(current, AfterSnapshot))
                throw new InvalidOperationException("Learner state changed after batch preparation; do not overwrite intervening evidence.");
            // A previous attempt may have committed and then thrown. Rewriting this exact snapshot is idempotent.
            if (!Equivalent(BeforeSnapshot, AfterSnapshot)) learner.LoadSnapshot(AfterSnapshot);
            committed = true;
        }
        private static bool Equivalent(LearnerProfileSnapshot left, LearnerProfileSnapshot right)
        {
            if (left.SchemaVersion != right.SchemaVersion || left.DefaultUnderstanding != right.DefaultUnderstanding ||
                left.Entries.Count != right.Entries.Count) return false;
            for (var index = 0; index < left.Entries.Count; index++)
            {
                if (!string.Equals(left.Entries[index].Text, right.Entries[index].Text, StringComparison.Ordinal) ||
                    left.Entries[index].Understanding != right.Entries[index].Understanding) return false;
            }
            return true;
        }
    }
}
