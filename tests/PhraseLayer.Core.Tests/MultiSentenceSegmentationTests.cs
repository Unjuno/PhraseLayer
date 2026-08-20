using System.Linq;
using PhraseLayer.Core.Semantics;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class MultiSentenceSegmentationTests
    {
        [Fact]
        public void SegmenterSplitsMultipleSentencesAndKeepsClausesInsideSentence()
        {
            const string source = "I was tired, so I went home. Then I slept!";
            var document = new RuleBasedSemanticSegmenter().Segment(source);
            var sentences = document.OfKind(SemanticUnitKind.Sentence).ToArray();
            var clauses = document.OfKind(SemanticUnitKind.Clause).ToArray();

            Assert.Equal(
                new[] { "I was tired, so I went home.", "Then I slept!" },
                sentences.Select(unit => unit.Text).ToArray());
            Assert.Equal(
                new[] { "I was tired", "so I went home", "Then I slept" },
                clauses.Select(unit => unit.Text).ToArray());

            foreach (var clause in clauses)
                Assert.Single(sentences.Where(sentence => sentence.Contains(clause)));
        }

        [Fact]
        public void SegmenterLeavesInterSentenceWhitespaceOutsideSentenceSpans()
        {
            const string source = "  First sentence. \n\t Second sentence without stop  ";
            var sentences = new RuleBasedSemanticSegmenter()
                .Segment(source)
                .OfKind(SemanticUnitKind.Sentence)
                .ToArray();

            Assert.Equal(2, sentences.Length);
            Assert.Equal("First sentence.", sentences[0].Text);
            Assert.Equal("Second sentence without stop", sentences[1].Text);
            Assert.Equal(" \n\t ", source.Substring(sentences[0].End, sentences[1].Start - sentences[0].End));
        }

        [Fact]
        public void SegmenterDoesNotTreatDecimalPointAsSentenceBoundary()
        {
            const string source = "Version 3.14 is stable. Next build starts.";
            var sentences = new RuleBasedSemanticSegmenter()
                .Segment(source)
                .OfKind(SemanticUnitKind.Sentence)
                .Select(unit => unit.Text)
                .ToArray();

            Assert.Equal(new[] { "Version 3.14 is stable.", "Next build starts." }, sentences);
        }

        [Fact]
        public void SegmenterKeepsTerminalRunsAndClosingQuoteWithSentence()
        {
            const string source = "Really?!\" Yes.";
            var document = new RuleBasedSemanticSegmenter().Segment(source);

            Assert.Equal(
                new[] { "Really?!\"", "Yes." },
                document.OfKind(SemanticUnitKind.Sentence).Select(unit => unit.Text).ToArray());
            Assert.Equal(
                new[] { "Really", "Yes" },
                document.OfKind(SemanticUnitKind.Clause).Select(unit => unit.Text).ToArray());
        }
    }
}
