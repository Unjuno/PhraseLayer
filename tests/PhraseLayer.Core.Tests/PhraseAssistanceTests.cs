using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PhraseAssistanceTests
    {
        [Fact]
        public void SegmenterUsesLongestConfiguredPhrasePattern()
        {
            var document = new RuleBasedSemanticSegmenter(
                    phrasePatterns: new[] { "fell asleep", "fell asleep immediately" })
                .Segment("I fell asleep immediately.");

            var phrase = Assert.Single(document.OfKind(SemanticUnitKind.Phrase));
            Assert.Equal("fell asleep immediately", phrase.Text, ignoreCase: true);
            Assert.Equal(3, phrase.TokenCount);
        }

        [Fact]
        public void ConfiguredPhraseCannotCrossClauseBoundaryAndShorterValidPatternStillMatches()
        {
            var document = new RuleBasedSemanticSegmenter(
                    phrasePatterns: new[] { "tired, so I", "so I" })
                .Segment("I was tired, so I left.");

            var phrase = Assert.Single(document.OfKind(SemanticUnitKind.Phrase));
            Assert.Equal("so I", phrase.Text, ignoreCase: true);
            var containingClause = Assert.Single(
                document.OfKind(SemanticUnitKind.Clause),
                clause => clause.Contains(phrase));
            Assert.Equal("so I left", containingClause.Text);
        }

        [Fact]
        public void PlannerCanSelectDifficultPhraseWhileComponentWordsRemainKnown()
        {
            const string source = "I fell asleep immediately.";
            var segmenter = new RuleBasedSemanticSegmenter(
                phrasePatterns: new[] { "fell asleep immediately" });
            var document = segmenter.Segment(source);
            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding("fell asleep immediately", 0.10);

            var plan = new AssistancePlanner().Plan(
                document,
                learner,
                AssistancePolicy.ForMode(AssistanceMode.Balanced));

            var decision = Assert.Single(plan.Decisions);
            Assert.Equal(SemanticUnitKind.Phrase, decision.Unit.Kind);
            Assert.Equal("fell asleep immediately", decision.Unit.Text, ignoreCase: true);
        }

        [Fact]
        public void DifficultPhraseReplacesContainedDifficultAtomsAsOneUnit()
        {
            const string source = "I fell asleep immediately.";
            var document = new RuleBasedSemanticSegmenter(
                    phrasePatterns: new[] { "fell asleep immediately" })
                .Segment(source);
            var learner = new InMemoryLearnerModel(0.10);
            learner.SetUnderstanding("I fell asleep immediately", 0.95);
            learner.SetUnderstanding("I", 0.95);
            learner.SetUnderstanding("fell asleep immediately", 0.40);

            var plan = new AssistancePlanner().Plan(
                document,
                learner,
                AssistancePolicy.ForMode(AssistanceMode.Balanced));

            var decision = Assert.Single(plan.Decisions);
            Assert.Equal(SemanticUnitKind.Phrase, decision.Unit.Kind);
            Assert.Equal("fell asleep immediately", decision.Unit.Text, ignoreCase: true);
        }

        [Fact]
        public async Task PipelineReplacesPhraseAsSingleSpanWithoutTouchingPunctuation()
        {
            const string source = "I fell asleep immediately.";
            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding("fell asleep immediately", 0.10);
            var pipeline = new LanguagePipeline(
                new RuleBasedSemanticSegmenter(
                    phrasePatterns: new[] { "fell asleep immediately" }),
                learner,
                new AssistancePlanner(),
                new DictionaryTranslationEngine(new Dictionary<string, string>
                {
                    ["fell asleep immediately"] = "すぐ眠ってしまった"
                }));

            var plan = await pipeline.PlanAsync(
                source,
                AssistancePolicy.ForMode(AssistanceMode.Balanced));

            Assert.Equal("I すぐ眠ってしまった.", plan.DisplayText);
            var assisted = Assert.Single(plan.Segments, segment => segment.IsAssisted);
            Assert.Equal(SemanticUnitKind.Phrase, assisted.Unit!.Kind);
        }

        [Fact]
        public void KnownPhraseProtectsContainedAtomsFromAutomaticAssistance()
        {
            const string source = "I fell asleep immediately.";
            var document = new RuleBasedSemanticSegmenter(
                    phrasePatterns: new[] { "fell asleep immediately" })
                .Segment(source);
            var learner = new InMemoryLearnerModel(0.10);
            learner.SetUnderstanding("I fell asleep immediately", 0.95);
            learner.SetUnderstanding("I", 0.95);
            learner.SetUnderstanding("fell asleep immediately", 0.98);

            var plan = new AssistancePlanner().Plan(
                document,
                learner,
                AssistancePolicy.ForMode(AssistanceMode.Balanced));

            Assert.Empty(plan.Decisions);
        }
    }
}
