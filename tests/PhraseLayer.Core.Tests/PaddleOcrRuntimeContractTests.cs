using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PaddleOcrRuntimeContractTests
    {
        [Fact]
        public void DetectorContractAcceptsSupportedDbLayouts()
        {
            AssertDetectorLayout(new[] { 1, 1, 4, 5 }, expectedWidth: 5, expectedHeight: 4);
            AssertDetectorLayout(new[] { 1, 4, 5 }, expectedWidth: 5, expectedHeight: 4);
            AssertDetectorLayout(new[] { 4, 5 }, expectedWidth: 5, expectedHeight: 4);
        }

        [Fact]
        public void DetectorContractRejectsAmbiguousBatchOrChannelShape()
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                PaddleOcrRuntimeContract.ValidateDetector(new[] { 1, 2, 4, 5 }, new float[40]));

            Assert.Contains("[1,1,H,W]", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RecognizerContractAcceptsBatchTimeClassAndBlankOffset()
        {
            var shape = new[] { 1, 12, 97 };
            var values = new float[12 * 97];

            var contract = PaddleOcrRuntimeContract.ValidateRecognizer(shape, values, dictionaryTokenCount: 96);

            Assert.Equal(12, contract.TimeSteps);
            Assert.Equal(97, contract.ClassCount);
            Assert.Equal(96, contract.DictionaryTokenCount);
            Assert.Equal(values.Length, contract.ValueCount);
            Assert.Equal(shape, contract.OutputShape);
            Assert.NotSame(shape, contract.OutputShape);
        }

        [Fact]
        public void RecognizerContractRejectsWrongRank()
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrRuntimeContract.ValidateRecognizer(new[] { 12, 97 }, new float[12 * 97], 96));

            Assert.Contains("[1,time,class]", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RecognizerContractRejectsDictionaryClassMismatch()
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrRuntimeContract.ValidateRecognizer(new[] { 1, 12, 97 }, new float[12 * 97], 95));

            Assert.Contains("dictionary token count + 1 CTC blank", exception.Message, StringComparison.Ordinal);
            Assert.Contains("classes=97", exception.Message, StringComparison.Ordinal);
            Assert.Contains("dictionary=95", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RecognizerContractRejectsValueCountMismatch()
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrRuntimeContract.ValidateRecognizer(new[] { 1, 12, 97 }, new float[100], 96));

            Assert.Contains("value count", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ReportDistinguishesUnobservedRecognizer()
        {
            var detector = PaddleOcrRuntimeContract.ValidateDetector(
                new[] { 1, 1, 4, 5 },
                new float[20]);

            var report = PaddleOcrRuntimeContract.BuildReport(detector, recognizer: null, dictionaryTokenCount: 96);

            Assert.Contains("detector shape=[1,1,4,5]", report, StringComparison.Ordinal);
            Assert.Contains("recognizer=unobserved", report, StringComparison.Ordinal);
            Assert.Contains("configured_dictionary=96", report, StringComparison.Ordinal);
        }

        private static void AssertDetectorLayout(int[] shape, int expectedWidth, int expectedHeight)
        {
            var values = new float[expectedWidth * expectedHeight];

            var contract = PaddleOcrRuntimeContract.ValidateDetector(shape, values);

            Assert.Equal(expectedWidth, contract.MapWidth);
            Assert.Equal(expectedHeight, contract.MapHeight);
            Assert.Equal(values.Length, contract.ValueCount);
            Assert.Equal(shape, contract.OutputShape);
            Assert.NotSame(shape, contract.OutputShape);
        }
    }
}
