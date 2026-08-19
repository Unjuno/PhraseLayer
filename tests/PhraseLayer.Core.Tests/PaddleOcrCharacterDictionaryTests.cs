using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PaddleOcrCharacterDictionaryTests
    {
        [Fact]
        public void ParsesLfDelimitedTokensWithoutSynthesizingTerminalEmptyLine()
        {
            var tokens = PaddleOcrCharacterDictionary.Parse("a\nb\n", useSpaceCharacter: false);

            Assert.Equal(new[] { "a", "b" }, tokens);
        }

        [Fact]
        public void PreservesEmptyLinesAndOrdinaryWhitespace()
        {
            var tokens = PaddleOcrCharacterDictionary.Parse("a\n\n  x  \n", useSpaceCharacter: false);

            Assert.Equal(new[] { "a", string.Empty, "  x  " }, tokens);
        }

        [Fact]
        public void RemovesCrFromCrLfTerminatorsOnlyAtLineEnd()
        {
            var tokens = PaddleOcrCharacterDictionary.Parse("a\r\nb\r\n", useSpaceCharacter: false);

            Assert.Equal(new[] { "a", "b" }, tokens);
        }

        [Fact]
        public void PreservesFinalLineWithoutLf()
        {
            var tokens = PaddleOcrCharacterDictionary.Parse("a\nb", useSpaceCharacter: false);

            Assert.Equal(new[] { "a", "b" }, tokens);
        }

        [Fact]
        public void SingleNewlineRepresentsOneEmptyDictionaryLine()
        {
            var tokens = PaddleOcrCharacterDictionary.Parse("\n", useSpaceCharacter: false);

            Assert.Equal(new[] { string.Empty }, tokens);
        }

        [Fact]
        public void EmptyFileProducesNoFileTokens()
        {
            var tokens = PaddleOcrCharacterDictionary.Parse(string.Empty, useSpaceCharacter: false);

            Assert.Empty(tokens);
        }

        [Fact]
        public void UseSpaceCharacterAppendsLiteralSpaceToken()
        {
            var tokens = PaddleOcrCharacterDictionary.Parse("a\nb\n", useSpaceCharacter: true);

            Assert.Equal(new[] { "a", "b", " " }, tokens);
        }

        [Fact]
        public void NullTextIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() =>
                PaddleOcrCharacterDictionary.Parse(null!, useSpaceCharacter: true));
        }
    }
}
