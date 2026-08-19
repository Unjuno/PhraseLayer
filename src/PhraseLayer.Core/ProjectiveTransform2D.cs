using System;

namespace PhraseLayer.Core.Inputs
{
    /// <summary>
    /// 3x3 homogeneous transform for 2D points. The final row is not assumed affine.
    /// </summary>
    public readonly struct ProjectiveTransform2D
    {
        public ProjectiveTransform2D(
            double m00, double m01, double m02,
            double m10, double m11, double m12,
            double m20, double m21, double m22)
        {
            M00 = m00; M01 = m01; M02 = m02;
            M10 = m10; M11 = m11; M12 = m12;
            M20 = m20; M21 = m21; M22 = m22;
        }

        public double M00 { get; }
        public double M01 { get; }
        public double M02 { get; }
        public double M10 { get; }
        public double M11 { get; }
        public double M12 { get; }
        public double M20 { get; }
        public double M21 { get; }
        public double M22 { get; }

        public ImagePoint Map(double x, double y)
        {
            var w = (M20 * x) + (M21 * y) + M22;
            if (Math.Abs(w) <= 1e-12)
                throw new InvalidOperationException("Projective transform maps the point to infinity.");

            return new ImagePoint(
                ((M00 * x) + (M01 * y) + M02) / w,
                ((M10 * x) + (M11 * y) + M12) / w);
        }
    }

    public static class ProjectiveTransformFactory
    {
        /// <summary>
        /// Builds the homography that maps the unit square corners
        /// (0,0),(1,0),(1,1),(0,1) onto p0,p1,p2,p3 respectively.
        /// This is the inverse-sampling transform needed by a perspective crop shader.
        /// </summary>
        public static ProjectiveTransform2D UnitSquareToQuad(ImageQuad quad)
        {
            ValidateFinite(quad.P0, nameof(quad));
            ValidateFinite(quad.P1, nameof(quad));
            ValidateFinite(quad.P2, nameof(quad));
            ValidateFinite(quad.P3, nameof(quad));

            var x0 = quad.P0.X; var y0 = quad.P0.Y;
            var x1 = quad.P1.X; var y1 = quad.P1.Y;
            var x2 = quad.P2.X; var y2 = quad.P2.Y;
            var x3 = quad.P3.X; var y3 = quad.P3.Y;

            var dx1 = x1 - x2;
            var dx2 = x3 - x2;
            var dx3 = x0 - x1 + x2 - x3;
            var dy1 = y1 - y2;
            var dy2 = y3 - y2;
            var dy3 = y0 - y1 + y2 - y3;

            double g;
            double h;
            if (Math.Abs(dx3) <= 1e-12 && Math.Abs(dy3) <= 1e-12)
            {
                g = 0.0;
                h = 0.0;
            }
            else
            {
                var denominator = (dx1 * dy2) - (dx2 * dy1);
                if (Math.Abs(denominator) <= 1e-12)
                    throw new ArgumentException("Quad does not define a stable projective transform.", nameof(quad));

                g = ((dx3 * dy2) - (dx2 * dy3)) / denominator;
                h = ((dx1 * dy3) - (dx3 * dy1)) / denominator;
            }

            return new ProjectiveTransform2D(
                x1 - x0 + (g * x1),
                x3 - x0 + (h * x3),
                x0,
                y1 - y0 + (g * y1),
                y3 - y0 + (h * y3),
                y0,
                g,
                h,
                1.0);
        }

        private static void ValidateFinite(ImagePoint point, string parameterName)
        {
            if (double.IsNaN(point.X) || double.IsInfinity(point.X) ||
                double.IsNaN(point.Y) || double.IsInfinity(point.Y))
            {
                throw new ArgumentException("Projective transform coordinates must be finite.", parameterName);
            }
        }
    }
}
