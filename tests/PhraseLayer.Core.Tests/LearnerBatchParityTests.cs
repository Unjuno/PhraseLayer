using System;
using System.Collections.Generic;
using System.Linq;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Semantics;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class LearnerBatchParityTests
    {
        [Fact]
        public void PreparedBatchesMatchSequentialPolicyFor2048DeterministicMixedEvidenceCases()
        {
            var random = new Random(50328);
            var words = new RuleBasedSemanticSegmenter().Segment("alpha beta, gamma delta.").OfKind(SemanticUnitKind.Word).ToArray();
            var boundaries = new[] { 0.0, 0.45, 0.48, 0.82, 0.99, 1.0 };
            for (var trial = 0; trial < 2048; trial++)
            {
                var sequential = new InMemoryLearnerModel(boundaries[trial % boundaries.Length]);
                foreach (var word in words) sequential.SetUnderstanding(word.Text, random.NextDouble());
                var staged = InMemoryLearnerModel.FromSnapshot(sequential.CreateSnapshot());
                var policy = new LearnerAdaptationPolicy(assistedExposureGain: random.NextDouble(),
                    unassistedCompletionGain: random.NextDouble(), recallSuccessGain: random.NextDouble(), recallFailureLoss: random.NextDouble());
                var events = new List<LearnerEvidence>();
                for (var index = 0; index < 1 + trial % 16; index++)
                    events.Add(new LearnerEvidence(words[random.Next(words.Length)], (LearningEvidenceKind)random.Next(7)));
                var expectedEngine = new LearnerAdaptationEngine(sequential, policy);
                var expected = events.Select(item => expectedEngine.Apply(item.Unit, item.Kind)).ToArray();
                var actual = new LearnerAdaptationEngine(staged, policy).PrepareBatch(events);
                Assert.Equal(expected.Length, actual.Updates.Count);
                for (var index = 0; index < expected.Length; index++)
                {
                    Assert.Equal(expected[index].PreviousUnderstanding, actual.Updates[index].PreviousUnderstanding);
                    Assert.Equal(expected[index].UpdatedUnderstanding, actual.Updates[index].UpdatedUnderstanding);
                }
                actual.Commit();
                Assert.Equal(sequential.CreateSnapshot().Entries.Select(item => (item.Text, item.Understanding)),
                    staged.CreateSnapshot().Entries.Select(item => (item.Text, item.Understanding)));
                actual.Commit();
                Assert.Equal(sequential.CreateSnapshot().Entries.Select(item => (item.Text, item.Understanding)),
                    staged.CreateSnapshot().Entries.Select(item => (item.Text, item.Understanding)));
            }
        }
    }
}
