using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PaddleCtcPackedOutputTests
    {
        [Fact]
        public void InterleavingPreservesBlankSeparatedDuplicatesAndConfidence()
        {
            var shape = new[] { 1, 6, 4 };
            var packed = new[] { 0f, 1f, 2f, .8f, 2f, .7f, 0f, 1f, 2f, .6f, 1f, 1f };
            var result = PaddleCtcPackedOutput.Unpack(shape, new[] { 1, 6, 2 }, packed);
            Assert.Equal(new[] { 0, 2, 2, 0, 2, 1 }, result.ClassIndices);
            Assert.Equal(new[] { 1f, .8f, .7f, 1f, .6f, 1f }, result.MaxScores);
            var decoded = PaddleCtcGreedyDecoder.DecodeFromIndices(result.ClassIndices, result.MaxScores, new[] { "a", "b", "c" });
            Assert.Equal("bba", decoded.Text);
            Assert.Equal(3, decoded.EmittedTokenCount);
            Assert.Equal(((double).8f + .6f + 1f) / 3, decoded.Confidence, 12);
            shape[2] = 99;
            packed[2] = 1f;
            Assert.Equal(4, result.OutputShape[2]);
            Assert.Equal(2, result.ClassIndices[1]);
        }

        [Fact]
        public void AllBlankOutputRemainsEmpty()
        {
            var result = PaddleCtcPackedOutput.Unpack(new[] { 1, 2, 2 }, new[] { 1, 2, 2 }, new[] { 0f, 1f, 0f, 1f });
            var decoded = PaddleCtcGreedyDecoder.DecodeFromIndices(result.ClassIndices, result.MaxScores, new[] { "a" });
            Assert.Equal(string.Empty, decoded.Text);
            Assert.Equal(0, decoded.EmittedTokenCount);
            Assert.Equal(0.0, decoded.Confidence);
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(.5f)]
        [InlineData(4f)]
        [InlineData(2147483648f)]
        [InlineData(float.MaxValue)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void InvalidClassValuesFailBeforeIntegerCast(float value)
        {
            Assert.Throws<InvalidOperationException>(() => PaddleCtcPackedOutput.Unpack(
                new[] { 1, 1, 4 }, new[] { 1, 1, 2 }, new[] { value, .9f }));
        }

        [Theory]
        [InlineData(-.1f)]
        [InlineData(1.1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void InvalidScoresAreRejectedEvenOnBlankTimesteps(float score)
        {
            Assert.Throws<InvalidOperationException>(() => PaddleCtcPackedOutput.Unpack(
                new[] { 1, 1, 4 }, new[] { 1, 1, 2 }, new[] { 0f, score }));
        }

        [Fact]
        public void InvalidDuplicateScoreCannotBeHiddenByCtcFiltering()
        {
            Assert.Throws<InvalidOperationException>(() => PaddleCtcPackedOutput.Unpack(
                new[] { 1, 2, 4 }, new[] { 1, 2, 2 }, new[] { 1f, .9f, 1f, float.NaN }));
        }

        [Fact]
        public void EqualElementCountDoesNotAcceptTransposedPackedLayout()
        {
            Assert.Throws<InvalidOperationException>(() => PaddleCtcPackedOutput.Unpack(
                new[] { 1, 3, 4 }, new[] { 1, 2, 3 }, new float[6]));
        }

        [Fact]
        public void RejectsWrongPackedRankBatchTimeAndPairWidth()
        {
            foreach (var packedShape in new[] { new[] { 3, 2 }, new[] { 2, 3, 2 }, new[] { 1, 2, 2 }, new[] { 1, 3, 3 } })
                Assert.Throws<InvalidOperationException>(() => PaddleCtcPackedOutput.Unpack(new[] { 1, 3, 4 }, packedShape, new float[6]));
        }

        [Fact]
        public void RejectsWrongWitnessRankBatchAndNonPositiveDimensions()
        {
            foreach (var shape in new[] { new[] { 3, 4 }, new[] { 2, 3, 4 }, new[] { 1, 0, 4 }, new[] { 1, -1, 4 }, new[] { 1, 3, 0 } })
                Assert.Throws<InvalidOperationException>(() => PaddleCtcPackedOutput.Unpack(shape, new[] { 1, 3, 2 }, new float[6]));
        }

        [Fact]
        public void RejectsTruncatedAndTrailingBuffers()
        {
            foreach (var length in new[] { 0, 5, 7 })
                Assert.Throws<InvalidOperationException>(() => PaddleCtcPackedOutput.Unpack(new[] { 1, 3, 4 }, new[] { 1, 3, 2 }, new float[length]));
        }

        [Fact]
        public void ChecksBufferLengthOverflowBeforeAllocation()
        {
            Assert.Throws<OverflowException>(() => PaddleCtcPackedOutput.Unpack(
                new[] { 1, int.MaxValue, 4 }, new[] { 1, int.MaxValue, 2 }, Array.Empty<float>()));
        }

        [Fact]
        public void AcceptsHighestReviewedExactFloatIndex()
        {
            var count = PaddleCtcPackedOutput.MaximumClassCount;
            var result = PaddleCtcPackedOutput.Unpack(new[] { 1, 1, count }, new[] { 1, 1, 2 }, new[] { (float)(count - 1), 1f });
            Assert.Equal(count - 1, result.ClassIndices[0]);
        }

        [Fact]
        public void RejectsUnreviewedClassCountEvenWhenWinnerIsSmall()
        {
            Assert.Throws<InvalidOperationException>(() => PaddleCtcPackedOutput.Unpack(
                new[] { 1, 1, PaddleCtcPackedOutput.MaximumClassCount + 1 }, new[] { 1, 1, 2 }, new[] { 0f, 1f }));
        }

        [Fact]
        public void RejectsNullInputs()
        {
            Assert.Throws<ArgumentNullException>(() => PaddleCtcPackedOutput.Unpack(null!, new[] { 1, 1, 2 }, new[] { 0f, 1f }));
            Assert.Throws<ArgumentNullException>(() => PaddleCtcPackedOutput.Unpack(new[] { 1, 1, 4 }, null!, new[] { 0f, 1f }));
            Assert.Throws<ArgumentNullException>(() => PaddleCtcPackedOutput.Unpack(new[] { 1, 1, 4 }, new[] { 1, 1, 2 }, null!));
        }

        [Fact]
        public void ReducedContractStillRejectsDictionaryMismatchAfterUnpack()
        {
            var result = PaddleCtcPackedOutput.Unpack(new[] { 1, 1, 4 }, new[] { 1, 1, 2 }, new[] { 1f, .9f });
            Assert.Throws<InvalidOperationException>(() => PaddleOcrRuntimeContract.ValidateRecognizerReduced(
                result.OutputShape, result.ClassIndices, result.MaxScores, dictionaryTokenCount: 2));
        }
    }
}
