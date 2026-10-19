using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PaddleOcrUnicodeWhitespaceTests
    {
        [Theory]
        [InlineData("\n")]
        [InlineData("\r\n")]
        public void UnicodeSpaceRetainsItsClassIndexThroughPackedCtc(string newline)
        {
            var dictionary = PaddleOcrCharacterDictionary.Parse(
                "A" + newline + "\u3000" + newline + "B" + newline, true);
            Assert.Equal(new[] { "A", "\u3000", "B", " " }, dictionary);
            var packed = PaddleCtcPackedOutput.Unpack(
                new[] { 1, 7, 5 }, new[] { 1, 7, 2 },
                new float[] { 1, 1, 2, 1, 2, 1, 0, 1, 2, 1, 3, 1, 4, 1 });
            var result = PaddleCtcGreedyDecoder.DecodeFromIndices(
                packed.ClassIndices, packed.MaxScores, dictionary);
            Assert.Equal("A\u3000\u3000B ", result.Text);
            Assert.Equal(5, result.EmittedTokenCount);
            Assert.Equal(1.0, result.Confidence);
        }
    }
}
