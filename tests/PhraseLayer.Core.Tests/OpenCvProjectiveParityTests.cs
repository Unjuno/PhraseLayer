using System;
using System.Collections.Generic;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    /// <summary>
    /// Golden parity fixtures generated with OpenCV 4.13.0 using:
    /// cv2.getPerspectiveTransform(
    ///     np.float32([[0,0],[1,0],[1,1],[0,1]]),
    ///     np.float32(sourceQuad))
    ///
    /// OpenCV receives float32 points, so the decimal-coordinate fixture uses a slightly
    /// looser tolerance than the integer-coordinate cases. These fixtures protect the
    /// inverse-sampling homography used by the Unity perspective crop shader.
    /// </summary>
    public sealed class OpenCvProjectiveParityTests
    {
        public static IEnumerable<object[]> GoldenCases()
        {
            yield return Case(
                "skewed",
                new ImageQuad(
                    new ImagePoint(10, 20),
                    new ImagePoint(120, 10),
                    new ImagePoint(110, 90),
                    new ImagePoint(0, 70)),
                new[]
                {
                    66.0, -10.0, 10.0,
                    -13.66666666666667, 47.66666666666667, 20.0,
                    -0.3666666666666666, -0.03333333333333333, 1.0,
                },
                1e-10);

            yield return Case(
                "trapezoid",
                new ImageQuad(
                    new ImagePoint(0, 0),
                    new ImagePoint(100, 0),
                    new ImagePoint(80, 100),
                    new ImagePoint(20, 100)),
                new[]
                {
                    100.0, 33.33333333333333, 0.0,
                    0.0, 166.66666666666666, 0.0,
                    0.0, 0.6666666666666667, 1.0,
                },
                1e-10);

            yield return Case(
                "fractional-perspective",
                new ImageQuad(
                    new ImagePoint(37.5, 22.25),
                    new ImagePoint(211.75, 31.5),
                    new ImagePoint(190.125, 144.875),
                    new ImagePoint(51.25, 130.625)),
                new[]
                {
                    158.4414888957455, 26.20889011961273, 37.5,
                    6.898320662177012, 140.1299760365739, 22.25,
                    -0.07465648691501550, 0.2431002950168338, 1.0,
                },
                2e-5);
        }

        [Theory]
        [MemberData(nameof(GoldenCases))]
        public void UnitSquareToQuadMatchesOpenCv413GoldenMatrix(
            string name,
            ImageQuad quad,
            double[] expected,
            double tolerance)
        {
            var actual = ProjectiveTransformFactory.UnitSquareToQuad(quad);
            var matrix = new[]
            {
                actual.M00, actual.M01, actual.M02,
                actual.M10, actual.M11, actual.M12,
                actual.M20, actual.M21, actual.M22,
            };

            Assert.Equal(expected.Length, matrix.Length);
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.InRange(
                    matrix[index],
                    expected[index] - tolerance,
                    expected[index] + tolerance);
            }

            // A matrix can be close component-wise yet still reveal a row/column convention bug
            // away from the four exact corners. Check interior points against the golden matrix too.
            AssertMappedPointMatchesGolden(actual, expected, 0.25, 0.25, tolerance * 10, name);
            AssertMappedPointMatchesGolden(actual, expected, 0.50, 0.50, tolerance * 10, name);
            AssertMappedPointMatchesGolden(actual, expected, 0.80, 0.20, tolerance * 10, name);
        }

        private static object[] Case(
            string name,
            ImageQuad quad,
            double[] expected,
            double tolerance)
        {
            return new object[] { name, quad, expected, tolerance };
        }

        private static void AssertMappedPointMatchesGolden(
            ProjectiveTransform2D actual,
            double[] expectedMatrix,
            double x,
            double y,
            double tolerance,
            string caseName)
        {
            var expectedW =
                (expectedMatrix[6] * x) +
                (expectedMatrix[7] * y) +
                expectedMatrix[8];
            Assert.True(Math.Abs(expectedW) > 1e-12, caseName + " golden matrix mapped a sample point to infinity.");

            var expectedX =
                ((expectedMatrix[0] * x) +
                 (expectedMatrix[1] * y) +
                 expectedMatrix[2]) / expectedW;
            var expectedY =
                ((expectedMatrix[3] * x) +
                 (expectedMatrix[4] * y) +
                 expectedMatrix[5]) / expectedW;

            var mapped = actual.Map(x, y);
            Assert.InRange(mapped.X, expectedX - tolerance, expectedX + tolerance);
            Assert.InRange(mapped.Y, expectedY - tolerance, expectedY + tolerance);
        }
    }
}
