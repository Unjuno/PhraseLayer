using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PaddleOcrDetectionPreprocessTests
    {
        [Fact]
        public void QuestCameraFrameAlreadyMatchesDefaultStrideGeometry()
        {
            var transform = PaddleOcrV6TinyDetectionPreprocess.CreateResizeTransform(1280, 960);

            Assert.Equal(1280, transform.ModelWidth);
            Assert.Equal(960, transform.ModelHeight);
            AssertClose(1.0, transform.RatioWidth);
            AssertClose(1.0, transform.RatioHeight);
            Assert.False(transform.UsesSmallImagePadding);
        }

        [Fact]
        public void DefaultMinSideResizeRoundsEachAxisToStrideIndependently()
        {
            var transform = PaddleOcrV6TinyDetectionPreprocess.CreateResizeTransform(640, 480);

            Assert.Equal(992, transform.ModelWidth);
            Assert.Equal(736, transform.ModelHeight);
            AssertClose(1.55, transform.RatioWidth);
            AssertClose(736.0 / 480.0, transform.RatioHeight);
            Assert.NotEqual(transform.RatioWidth, transform.RatioHeight);

            var source = new ImagePoint(320.0, 240.0);
            var model = transform.SourceToModel(source);
            var roundTrip = transform.ModelToSource(model);

            AssertClose(496.0, model.X);
            AssertClose(368.0, model.Y);
            AssertPoint(source, roundTrip);
        }

        [Fact]
        public void MaxSideLimitIsAppliedBeforePythonCompatibleStrideRounding()
        {
            var transform = PaddleOcrV6TinyDetectionPreprocess.CreateResizeTransform(10000, 1000);

            Assert.Equal(4000, transform.ModelWidth);
            Assert.Equal(384, transform.ModelHeight);
            AssertClose(0.4, transform.RatioWidth);
            AssertClose(0.384, transform.RatioHeight);
        }

        [Fact]
        public void VerySmallInputIsPaddedAtTopLeftBeforeResize()
        {
            var transform = PaddleOcrV6TinyDetectionPreprocess.CreateResizeTransform(20, 10);

            Assert.True(transform.UsesSmallImagePadding);
            Assert.Equal(32, transform.PaddedWidth);
            Assert.Equal(32, transform.PaddedHeight);
            Assert.Equal(736, transform.ModelWidth);
            Assert.Equal(736, transform.ModelHeight);
            AssertClose(23.0, transform.RatioWidth);
            AssertClose(23.0, transform.RatioHeight);

            var originalBottomRight = transform.SourceToModel(new ImagePoint(20.0, 10.0));
            AssertClose(460.0, originalBottomRight.X);
            AssertClose(230.0, originalBottomRight.Y);
        }

        [Theory]
        [InlineData(PaddleDetLimitType.Max)]
        [InlineData(PaddleDetLimitType.ResizeLong)]
        public void AlternativePaddleLimitModesUseLongSideTarget(PaddleDetLimitType limitType)
        {
            var transform = PaddleOcrV6TinyDetectionPreprocess.CreateResizeTransform(
                1280,
                960,
                736,
                limitType,
                4000,
                32);

            Assert.Equal(736, transform.ModelWidth);
            Assert.Equal(544, transform.ModelHeight);
        }

        [Fact]
        public void OfficialBgrNormalizationConstantsAreAppliedPerChannel()
        {
            var black = PaddleOcrV6TinyDetectionPreprocess.NormalizeBgr(0, 0, 0);
            var white = PaddleOcrV6TinyDetectionPreprocess.NormalizeBgr(255, 255, 255);

            AssertClose(-0.485 / 0.229, black.Channel0, 1e-5);
            AssertClose(-0.456 / 0.224, black.Channel1, 1e-5);
            AssertClose(-0.406 / 0.225, black.Channel2, 1e-5);
            AssertClose((1.0 - 0.485) / 0.229, white.Channel0, 1e-5);
            AssertClose((1.0 - 0.456) / 0.224, white.Channel1, 1e-5);
            AssertClose((1.0 - 0.406) / 0.225, white.Channel2, 1e-5);
        }

        [Fact]
        public void OutputMapperAcceptsPaddleResizeTransform()
        {
            var transform = PaddleOcrV6TinyDetectionPreprocess.CreateResizeTransform(640, 480);
            var sourceQuad = ImageQuad.FromRect(100.0, 120.0, 220.0, 80.0);
            var detection = new OcrDetectionCandidate(transform.SourceToModel(sourceQuad), 0.93);
            var recognition = new OcrRecognitionCandidate("exit", 0.88);

            var region = OcrModelOutputMapper.ToSourceRegion(detection, recognition, transform);

            Assert.Equal("exit", region.Text);
            AssertClose(0.88, region.Confidence);
            AssertPoint(sourceQuad.P0, region.ImageBounds.P0);
            AssertPoint(sourceQuad.P2, region.ImageBounds.P2);
        }

        [Fact]
        public void InvalidDimensionsModesAndChannelsAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PaddleOcrV6TinyDetectionPreprocess.CreateResizeTransform(0, 960));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PaddleOcrV6TinyDetectionPreprocess.CreateResizeTransform(
                    640, 480, 736, (PaddleDetLimitType)99, 4000, 32));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PaddleOcrV6TinyDetectionPreprocess.NormalizeChannel(0, 3));
        }

        private static void AssertPoint(ImagePoint expected, ImagePoint actual)
        {
            AssertClose(expected.X, actual.X);
            AssertClose(expected.Y, actual.Y);
        }

        private static void AssertClose(double expected, double actual, double tolerance = 1e-9)
        {
            Assert.InRange(actual, expected - tolerance, expected + tolerance);
        }
    }
}
