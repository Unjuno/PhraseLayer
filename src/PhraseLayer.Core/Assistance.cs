using System;
using System.Collections.Generic;
using System.Linq;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Semantics;

namespace PhraseLayer.Core.Assistance
{
    public enum AssistanceMode { Auto = 0, Easy = 1, Balanced = 2, Challenge = 3, Immersion = 4 }

    public sealed class AssistancePolicy
    {
        private AssistancePolicy(AssistanceMode mode, double? targetRatio)
        {
            Mode = mode; TargetAssistanceRatio = targetRatio;
            PreserveKnownThreshold = 0.82;
            PhraseReplacementUnderstandingThreshold = 0.55;
            ClauseReplacementUnderstandingThreshold = 0.48;
            MaxPhraseTokens = 8;
            MaxClauseTokens = 12;
        }
        public AssistanceMode Mode { get; }
        public double? TargetAssistanceRatio { get; }
        public double PreserveKnownThreshold { get; set; }
        public double PhraseReplacementUnderstandingThreshold { get; set; }
        public double ClauseReplacementUnderstandingThreshold { get; set; }
        public int MaxPhraseTokens { get; set; }
        public int MaxClauseTokens { get; set; }
        public static AssistancePolicy ForMode(AssistanceMode mode)
        {
            switch (mode)
            {
                case AssistanceMode.Easy: return new AssistancePolicy(mode, 0.70);
                case AssistanceMode.Balanced: return new AssistancePolicy(mode, 0.45);
                case AssistanceMode.Challenge: return new AssistancePolicy(mode, 0.25);
                case AssistanceMode.Immersion: return new AssistancePolicy(mode, 0.05);
                default: return new AssistancePolicy(AssistanceMode.Auto, null);
            }
        }
    }

    public sealed class AssistanceDecision
    {
        public AssistanceDecision(SemanticUnit unit, double understanding) { Unit = unit; EstimatedUnderstanding = understanding; }
        public SemanticUnit Unit { get; }
        public double EstimatedUnderstanding { get; }
        public double Difficulty => 1.0 - EstimatedUnderstanding;
    }

    public sealed class AssistancePlan
    {
        public AssistancePlan(IReadOnlyList<AssistanceDecision> decisions, double targetRatio, double selectedRatio)
        { Decisions = decisions; TargetRatio = targetRatio; SelectedRatio = selectedRatio; }
        public IReadOnlyList<AssistanceDecision> Decisions { get; }
        public double TargetRatio { get; }
        public double SelectedRatio { get; }
    }

    public sealed class AssistancePlanner
    {
        public AssistancePlan Plan(SemanticDocument document, ILearnerModel learner, AssistancePolicy policy)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (learner == null) throw new ArgumentNullException(nameof(learner));
            if (policy == null) throw new ArgumentNullException(nameof(policy));

            var atomicUnits = BuildAtomicUnits(document).ToArray();
            if (atomicUnits.Length == 0) return new AssistancePlan(Array.Empty<AssistanceDecision>(), 0, 0);
            var totalTokens = atomicUnits.Sum(unit => unit.TokenCount);
            var estimates = atomicUnits.ToDictionary(unit => unit.Id, learner.Estimate);
            var candidates = new List<AssistanceDecision>();
            var clauses = document.OfKind(SemanticUnitKind.Clause).ToArray();
            var phrases = document.OfKind(SemanticUnitKind.Phrase).ToArray();

            foreach (var clause in clauses)
            {
                var atoms = atomicUnits.Where(clause.Contains).ToArray();
                if (atoms.Length == 0) continue;
                var explicitClause = learner.Estimate(clause);
                var understanding = explicitClause.IsExplicit ? explicitClause.Understanding : WeightedUnderstanding(atoms, estimates);
                if (clause.TokenCount <= policy.MaxClauseTokens && understanding < policy.ClauseReplacementUnderstandingThreshold)
                {
                    candidates.Add(new AssistanceDecision(clause, understanding));
                }
                else
                {
                    foreach (var phrase in phrases.Where(clause.Contains))
                    {
                        var phraseAtoms = atoms.Where(phrase.Contains).ToArray();
                        if (phraseAtoms.Length == 0) continue;
                        var explicitPhrase = learner.Estimate(phrase);
                        var phraseUnderstanding = explicitPhrase.IsExplicit
                            ? explicitPhrase.Understanding
                            : WeightedUnderstanding(phraseAtoms, estimates);
                        if (phrase.TokenCount <= policy.MaxPhraseTokens &&
                            phraseUnderstanding < policy.PhraseReplacementUnderstandingThreshold)
                        {
                            candidates.Add(new AssistanceDecision(phrase, phraseUnderstanding));
                        }
                    }

                    foreach (var atom in atoms)
                    {
                        var estimate = estimates[atom.Id];
                        if (estimate.Understanding < policy.PreserveKnownThreshold) candidates.Add(new AssistanceDecision(atom, estimate.Understanding));
                    }
                }
            }

            if (clauses.Length == 0)
            {
                foreach (var phrase in phrases)
                {
                    var phraseAtoms = atomicUnits.Where(phrase.Contains).ToArray();
                    if (phraseAtoms.Length == 0) continue;
                    var explicitPhrase = learner.Estimate(phrase);
                    var phraseUnderstanding = explicitPhrase.IsExplicit
                        ? explicitPhrase.Understanding
                        : WeightedUnderstanding(phraseAtoms, estimates);
                    if (phrase.TokenCount <= policy.MaxPhraseTokens &&
                        phraseUnderstanding < policy.PhraseReplacementUnderstandingThreshold)
                    {
                        candidates.Add(new AssistanceDecision(phrase, phraseUnderstanding));
                    }
                }

                foreach (var atom in atomicUnits)
                {
                    var estimate = estimates[atom.Id];
                    if (estimate.Understanding < policy.PreserveKnownThreshold) candidates.Add(new AssistanceDecision(atom, estimate.Understanding));
                }
            }

            candidates = candidates.GroupBy(item => item.Unit.Start + ":" + item.Unit.Length)
                .Select(group => group.OrderByDescending(item => item.Unit.Kind).First())
                .OrderByDescending(item => item.Difficulty).ThenByDescending(item => item.Unit.Kind).ThenBy(item => item.Unit.Start).ToList();

            var averageDifficulty = atomicUnits.Sum(unit => (1.0 - estimates[unit.Id].Understanding) * unit.TokenCount) / totalTokens;
            var targetRatio = policy.TargetAssistanceRatio ?? Clamp(averageDifficulty, 0.10, 0.75);
            var targetTokens = (int)Math.Ceiling(totalTokens * targetRatio);
            var selected = new List<AssistanceDecision>();
            var selectedTokens = 0;
            foreach (var candidate in candidates)
            {
                if (selected.Any(existing => existing.Unit.Overlaps(candidate.Unit))) continue;
                if (selectedTokens >= targetTokens && selected.Count > 0) break;
                selected.Add(candidate);
                selectedTokens += candidate.Unit.TokenCount;
            }
            selected.Sort((left, right) => left.Unit.Start.CompareTo(right.Unit.Start));
            var selectedRatio = Math.Min(1.0, (double)selectedTokens / totalTokens);
            return new AssistancePlan(selected, targetRatio, selectedRatio);
        }

        private static IEnumerable<SemanticUnit> BuildAtomicUnits(SemanticDocument document)
        {
            var mwes = document.OfKind(SemanticUnitKind.MultiwordExpression).OrderBy(unit => unit.Start).ToArray();
            foreach (var mwe in mwes) yield return mwe;
            foreach (var word in document.OfKind(SemanticUnitKind.Word)) if (!mwes.Any(mwe => mwe.Overlaps(word))) yield return word;
        }
        private static double WeightedUnderstanding(IEnumerable<SemanticUnit> units, IReadOnlyDictionary<string, KnowledgeEstimate> estimates)
        {
            var tokens = 0; var sum = 0.0;
            foreach (var unit in units) { tokens += unit.TokenCount; sum += estimates[unit.Id].Understanding * unit.TokenCount; }
            return tokens == 0 ? 1.0 : sum / tokens;
        }
        private static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
    }
}
