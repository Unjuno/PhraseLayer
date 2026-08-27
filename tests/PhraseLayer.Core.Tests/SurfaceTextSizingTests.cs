using System;
using PhraseLayer.Core.Spatial;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class SurfaceTextSizingTests
    {
        [Fact]
        public void JapaneseGlyphsEstimateWiderThanSameCountLatinGlyphs()
        {
            Assert.Equal(1.2, SurfaceTextSizing.EstimateEmWidth("ab"), 6);
            Assert.Equal(2.0, SurfaceTextSizing.EstimateEmWidth("日本"), 6);
        }

        [Fact]
        public void CombiningMarkDoesNotConsumeAdditionalWidth()
        {
            Assert.Equal(
                SurfaceTextSizing.EstimateEmWidth("e"),
                SurfaceTextSizing.EstimateEmWidth("e\u0301"),
                6);
        }

        [Fact]
        public void SurrogatePairEmojiCountsAsOneWideGlyph()
        {
            Assert.Equal(1.0, SurfaceTextSizing.EstimateEmWidth("\U0001F600"), 6);
        }

        [Fact]
        public void PhysicalFitUsesEstimatedGlyphWidthRatherThanUtf16Length()
        {
            var layout = new SurfaceTextLayout(
                new SpatialVector3(0.0, 0.0, 1.0),
                new SpatialVector3(1.0, 0.0, 0.0),
                new SpatialVector3(0.0, 1.0, 0.0),
                new SpatialVector3(0.0, 0.0, -1.0),
                widthMeters: 0.12,
                heightMeters: 0.10);

            var size = SurfaceTextSizing.ComputeCharacterSizeMeters(
                "abcdef",
                layout,
                heightFraction: 0.85,
                widthFraction: 0.95);

            Assert.Equal((0.12 * 0.95) / 3.6, size, 6);
        }

        [Fact]
        public void InvalidFitParametersFailClosed()
        {
            var layout = new SurfaceTextLayout(
                new SpatialVector3(0.0, 0.0, 1.0),
                new SpatialVector3(1.0, 0.0, 0.0),
                new SpatialVector3(0.0, 1.0, 0.0),
                new SpatialVector3(0.0, 0.0, -1.0),
                widthMeters: 0.12,
                heightMeters: 0.10);

            Assert.Throws<ArgumentNullException>(() => SurfaceTextSizing.EstimateEmWidth(null!));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SurfaceTextSizing.ComputeCharacterSizeMeters("text", layout, 0.0, 0.95));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SurfaceTextSizing.ComputeCharacterSizeMeters("text", layout, 0.85, 1.1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SurfaceTextSizing.ComputeCharacterSizeMeters("text", layout, 0.85, 0.95, 0.0));
        }
    }
}
