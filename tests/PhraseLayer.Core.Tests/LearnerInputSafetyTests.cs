using System;
using System.Linq;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class LearnerInputSafetyTests
    {
        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void NonFiniteScoresAreRejectedAtEveryEntryPointWithoutMutation(double score)
        {
            var unit = Word("alpha");
            Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryLearnerModel(score));
            Assert.Throws<ArgumentOutOfRangeException>(() => new KnowledgeEstimate(unit, score, false));
            var model = new InMemoryLearnerModel(0.5);
            model.SetUnderstanding("alpha", 0.4);
            Assert.Throws<ArgumentOutOfRangeException>(() => model.SetUnderstanding("alpha", score));
            Assert.Equal(0.4, model.Estimate(unit).Understanding);
            Assert.Equal(1, model.ExplicitEntryCount);
            Assert.Throws<ArgumentOutOfRangeException>(() => model.SetUnderstanding("beta", score));
            Assert.Equal(1, model.ExplicitEntryCount);
            Assert.Single(model.CreateSnapshot().Entries);
        }
        [Fact]
        public void EstimateRejectsNullUnit() => Assert.Throws<ArgumentNullException>(() => new KnowledgeEstimate(null!, 0.5, false));

        [Theory]
        [InlineData(-1.0, 0.0)]
        [InlineData(0.0, 0.0)]
        [InlineData(0.5, 0.5)]
        [InlineData(1.0, 1.0)]
        [InlineData(2.0, 1.0)]
        public void FiniteScoresPreserveClampingAndRoundTrip(double input, double expected)
        {
            var unit = Word("alpha");
            var model = new InMemoryLearnerModel(input);
            Assert.Equal(expected, model.DefaultUnderstanding);
            model.SetUnderstanding("alpha", input);
            Assert.Equal(expected, new KnowledgeEstimate(unit, input, false).Understanding);
            Assert.Equal(expected, InMemoryLearnerModel.FromSnapshot(model.CreateSnapshot()).Estimate(unit).Understanding);
        }
        [Theory]
        [InlineData(-1)]
        [InlineData(7)]
        [InlineData(int.MaxValue)]
        public void InvalidEvidenceCannotEnterPendingStateOrEraseValidEvidence(int invalid)
        {
            const string source = "alpha beta.";
            var doc = new RuleBasedSemanticSegmenter().Segment(source);
            var words = doc.OfKind(SemanticUnitKind.Word).ToArray();
            var model = new InMemoryLearnerModel(0.5);
            var plan = new MixedLanguagePlan(source, new[] { new MixedLanguageSegment(source, source, false, null) },
                new AssistancePlan(Array.Empty<AssistanceDecision>(), 0, 0), doc);
            var session = new LearningEncounterSession(plan, new LearnerAdaptationEngine(model));
            session.Record(words[0], LearningEvidenceKind.RecallSucceeded);
            Assert.Throws<ArgumentOutOfRangeException>(() => session.Record(words[0], (LearningEvidenceKind)invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.RecordAt(words[1].Start, (LearningEvidenceKind)invalid));
            Assert.Equal(0.5, model.Estimate(words[0]).Understanding);
            var summary = session.Finish();
            Assert.Single(summary.Updates);
            Assert.Equal(0.6, model.Estimate(words[0]).Understanding, 12);
            Assert.Equal(0.5, model.Estimate(words[1]).Understanding);
            Assert.Same(summary, session.Finish());
            Assert.Equal(0.6, model.Estimate(words[0]).Understanding, 12);
        }
        private static SemanticUnit Word(string text) => new SemanticUnit("word:0", SemanticUnitKind.Word, 0, text.Length, text, 1);
    }
}
