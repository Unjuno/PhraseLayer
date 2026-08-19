using System;
using System.Collections.Generic;
using System.Linq;

namespace PhraseLayer.Core.Inputs
{
    /// <summary>
    /// A single-channel detector probability map in row-major top-left image coordinates.
    /// </summary>
    public sealed class PaddleDbProbabilityMap
    {
        public PaddleDbProbabilityMap(float[] values, int width, int height)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (values.Length != checked(width * height))
                throw new ArgumentException("Probability map length must equal width * height.", nameof(values));

            Values = values;
            Width = width;
            Height = height;
        }

        public float[] Values { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>
        /// Accepts the common PP-OCR detector output layouts [1,1,H,W], [1,H,W], or [H,W].
        /// No layout is guessed when batch/channel dimensions are not exactly one.
        /// </summary>
        public static PaddleDbProbabilityMap FromTensor(int[] shape, float[] values)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (values == null) throw new ArgumentNullException(nameof(values));

            int height;
            int width;
            switch (shape.Length)
            {
                case 4:
                    if (shape[0] != 1 || shape[1] != 1)
                        throw new ArgumentException("Rank-4 DB output must be [1,1,H,W].", nameof(shape));
                    height = shape[2];
                    width = shape[3];
                    break;
                case 3:
                    if (shape[0] != 1)
                        throw new ArgumentException("Rank-3 DB output must be [1,H,W].", nameof(shape));
                    height = shape[1];
                    width = shape[2];
                    break;
                case 2:
                    height = shape[0];
                    width = shape[1];
                    break;
                default:
                    throw new ArgumentException("DB output rank must be 2, 3, or 4.", nameof(shape));
            }

            if (width <= 0 || height <= 0)
                throw new ArgumentException("DB output spatial dimensions must be positive.", nameof(shape));
            if (values.Length != checked(width * height))
                throw new ArgumentException("DB output value count does not match its spatial shape.", nameof(values));

            return new PaddleDbProbabilityMap(values, width, height);
        }
    }

    public sealed class PaddleDbQuadDetection
    {
        public PaddleDbQuadDetection(ImageQuad imageBounds, double score)
        {
            if (double.IsNaN(score) || double.IsInfinity(score) || score < 0.0 || score > 1.0)
                throw new ArgumentOutOfRangeException(nameof(score));
            ImageBounds = imageBounds;
            Score = score;
        }

        public ImageQuad ImageBounds { get; }
        public double Score { get; }
    }

    /// <summary>
    /// Dependency-free DB quad postprocessor for Quest/Unity use.
    ///
    /// PaddleOCR's orchestration is preserved: prediction threshold -> candidate geometry ->
    /// minimum-area rectangle -> fast box score -> unclip distance -> expanded rectangle ->
    /// minimum-short-side filter -> destination scaling.
    ///
    /// Geometry backend note: PaddleOCR uses OpenCV findContours/minAreaRect and pyclipper JT_ROUND.
    /// This baseline uses 8-connected foreground components, a convex hull, an exact rotating-edge
    /// minimum-area rectangle, and expands that rectangle by the same unclip distance. For quad mode,
    /// the downstream minimum-area rectangle of a round offset of a rectangle has the same extents,
    /// but contour enumeration and raster-score edge behavior are not claimed pixel-identical until
    /// fixture parity is measured against OpenCV/pyclipper.
    /// </summary>
    public sealed class PaddleDbQuadPostprocessor
    {
        private readonly PaddleDbPostprocessSpec spec;

        public PaddleDbQuadPostprocessor(PaddleDbPostprocessSpec spec)
        {
            this.spec = spec ?? throw new ArgumentNullException(nameof(spec));
            if (spec.ScoreMode != PaddleDbScoreMode.Fast)
            {
                throw new NotSupportedException(
                    "The dependency-free DB backend currently implements PaddleOCR fast box scoring only.");
            }
        }

        public IReadOnlyList<PaddleDbQuadDetection> Process(
            PaddleDbProbabilityMap probabilityMap,
            int destinationWidth,
            int destinationHeight)
        {
            if (probabilityMap == null) throw new ArgumentNullException(nameof(probabilityMap));
            if (destinationWidth <= 0) throw new ArgumentOutOfRangeException(nameof(destinationWidth));
            if (destinationHeight <= 0) throw new ArgumentOutOfRangeException(nameof(destinationHeight));

            ValidateProbabilityMap(probabilityMap.Values);
            var foreground = BuildForegroundMask(probabilityMap);
            var components = ExtractEightConnectedComponents(
                foreground,
                probabilityMap.Width,
                probabilityMap.Height,
                spec.MaxCandidates);

            var detections = new List<PaddleDbQuadDetection>(components.Count);
            foreach (var component in components)
            {
                var hull = ConvexHull(component);
                if (hull.Count < 3)
                    continue;

                var rectangle = MinimumAreaRectangle.FromHull(hull);
                if (!spec.AcceptShortSide(rectangle.ShortSide, afterUnclip: false))
                    continue;

                var score = BoxScoreFast(
                    probabilityMap.Values,
                    probabilityMap.Width,
                    probabilityMap.Height,
                    rectangle.Corners);
                if (!spec.AcceptBoxScore(score))
                    continue;

                var distance = spec.ComputeUnclipDistance(rectangle.Area, rectangle.Perimeter);
                var expanded = rectangle.Expand(distance);
                if (!spec.AcceptShortSide(expanded.ShortSide, afterUnclip: true))
                    continue;

                var scaled = ScaleQuad(
                    expanded.Corners,
                    probabilityMap.Width,
                    probabilityMap.Height,
                    destinationWidth,
                    destinationHeight);
                detections.Add(new PaddleDbQuadDetection(scaled, score));
            }

            return detections;
        }

        private bool[] BuildForegroundMask(PaddleDbProbabilityMap map)
        {
            var mask = new bool[map.Values.Length];
            for (var index = 0; index < map.Values.Length; index++)
                mask[index] = spec.IsForeground(map.Values[index]);
            return mask;
        }

        private static void ValidateProbabilityMap(float[] values)
        {
            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(values),
                        "DB probability values must be finite and within [0,1].");
                }
            }
        }

        private static List<List<DbPoint>> ExtractEightConnectedComponents(
            bool[] mask,
            int width,
            int height,
            int maxCandidates)
        {
            var visited = new bool[mask.Length];
            var components = new List<List<DbPoint>>();
            var queue = new Queue<int>();

            for (var y = 0; y < height && components.Count < maxCandidates; y++)
            {
                for (var x = 0; x < width && components.Count < maxCandidates; x++)
                {
                    var seed = (y * width) + x;
                    if (!mask[seed] || visited[seed])
                        continue;

                    var component = new List<DbPoint>();
                    visited[seed] = true;
                    queue.Enqueue(seed);

                    while (queue.Count > 0)
                    {
                        var current = queue.Dequeue();
                        var currentY = current / width;
                        var currentX = current - (currentY * width);
                        component.Add(new DbPoint(currentX, currentY));

                        var minY = Math.Max(0, currentY - 1);
                        var maxY = Math.Min(height - 1, currentY + 1);
                        var minX = Math.Max(0, currentX - 1);
                        var maxX = Math.Min(width - 1, currentX + 1);
                        for (var neighborY = minY; neighborY <= maxY; neighborY++)
                        {
                            for (var neighborX = minX; neighborX <= maxX; neighborX++)
                            {
                                if (neighborX == currentX && neighborY == currentY)
                                    continue;
                                var neighbor = (neighborY * width) + neighborX;
                                if (!mask[neighbor] || visited[neighbor])
                                    continue;
                                visited[neighbor] = true;
                                queue.Enqueue(neighbor);
                            }
                        }
                    }

                    components.Add(component);
                }
            }

            return components;
        }

        private static List<DbPoint> ConvexHull(List<DbPoint> points)
        {
            if (points.Count <= 1)
                return new List<DbPoint>(points);

            var sorted = points
                .Distinct()
                .OrderBy(point => point.X)
                .ThenBy(point => point.Y)
                .ToList();
            if (sorted.Count <= 2)
                return sorted;

            var lower = new List<DbPoint>();
            foreach (var point in sorted)
            {
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], point) <= 0.0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(point);
            }

            var upper = new List<DbPoint>();
            for (var index = sorted.Count - 1; index >= 0; index--)
            {
                var point = sorted[index];
                while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], point) <= 0.0)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(point);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static double BoxScoreFast(float[] probability, int width, int height, IReadOnlyList<DbPoint> box)
        {
            var xmin = Clamp((int)Math.Floor(box.Min(point => point.X)), 0, width - 1);
            var xmax = Clamp((int)Math.Ceiling(box.Max(point => point.X)), 0, width - 1);
            var ymin = Clamp((int)Math.Floor(box.Min(point => point.Y)), 0, height - 1);
            var ymax = Clamp((int)Math.Ceiling(box.Max(point => point.Y)), 0, height - 1);

            var integerBox = new DbIntPoint[box.Count];
            for (var index = 0; index < box.Count; index++)
            {
                integerBox[index] = new DbIntPoint(
                    (int)(box[index].X - xmin),
                    (int)(box[index].Y - ymin));
            }

            double sum = 0.0;
            var count = 0;
            for (var y = ymin; y <= ymax; y++)
            {
                for (var x = xmin; x <= xmax; x++)
                {
                    if (!PointInPolygonInclusive(x - xmin, y - ymin, integerBox))
                        continue;
                    sum += probability[(y * width) + x];
                    count++;
                }
            }

            return count == 0 ? 0.0 : sum / count;
        }

        private static bool PointInPolygonInclusive(double x, double y, IReadOnlyList<DbIntPoint> polygon)
        {
            var inside = false;
            for (var i = 0; i < polygon.Count; i++)
            {
                var j = (i + polygon.Count - 1) % polygon.Count;
                var a = polygon[j];
                var b = polygon[i];
                if (PointOnSegment(x, y, a, b))
                    return true;

                var intersects = ((a.Y > y) != (b.Y > y)) &&
                                 (x < ((double)(b.X - a.X) * (y - a.Y) / (b.Y - a.Y)) + a.X);
                if (intersects)
                    inside = !inside;
            }
            return inside;
        }

        private static bool PointOnSegment(double x, double y, DbIntPoint a, DbIntPoint b)
        {
            var cross = ((x - a.X) * (b.Y - a.Y)) - ((y - a.Y) * (b.X - a.X));
            if (Math.Abs(cross) > 1e-9)
                return false;
            return x >= Math.Min(a.X, b.X) && x <= Math.Max(a.X, b.X) &&
                   y >= Math.Min(a.Y, b.Y) && y <= Math.Max(a.Y, b.Y);
        }

        private static ImageQuad ScaleQuad(
            IReadOnlyList<DbPoint> points,
            int bitmapWidth,
            int bitmapHeight,
            int destinationWidth,
            int destinationHeight)
        {
            if (points.Count != 4)
                throw new ArgumentException("DB quad must contain exactly four points.", nameof(points));

            var scaled = new ImagePoint[4];
            for (var index = 0; index < 4; index++)
            {
                var destination = PaddleDbPostprocessSpec.ScaleBitmapPoint(
                    new DbBitmapPoint(points[index].X, points[index].Y),
                    bitmapWidth,
                    bitmapHeight,
                    destinationWidth,
                    destinationHeight);
                scaled[index] = new ImagePoint(destination.X, destination.Y);
            }

            return new ImageQuad(scaled[0], scaled[1], scaled[2], scaled[3]);
        }

        private static double Cross(DbPoint origin, DbPoint a, DbPoint b)
        {
            return ((a.X - origin.X) * (b.Y - origin.Y)) -
                   ((a.Y - origin.Y) * (b.X - origin.X));
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }

        private readonly struct DbIntPoint
        {
            public DbIntPoint(int x, int y)
            {
                X = x;
                Y = y;
            }
            public int X { get; }
            public int Y { get; }
        }

        private readonly struct DbPoint : IEquatable<DbPoint>
        {
            public DbPoint(double x, double y)
            {
                X = x;
                Y = y;
            }
            public double X { get; }
            public double Y { get; }

            public bool Equals(DbPoint other) => X.Equals(other.X) && Y.Equals(other.Y);
            public override bool Equals(object obj) => obj is DbPoint other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    return (X.GetHashCode() * 397) ^ Y.GetHashCode();
                }
            }
        }

        private sealed class MinimumAreaRectangle
        {
            private MinimumAreaRectangle(
                double centerX,
                double centerY,
                double angle,
                double width,
                double height)
            {
                CenterX = centerX;
                CenterY = centerY;
                Angle = angle;
                Width = width;
                Height = height;
                Corners = OrderLikePaddle(CreateCorners(centerX, centerY, angle, width, height));
            }

            public double CenterX { get; }
            public double CenterY { get; }
            public double Angle { get; }
            public double Width { get; }
            public double Height { get; }
            public double ShortSide => Math.Min(Width, Height);
            public double Area => Width * Height;
            public double Perimeter => 2.0 * (Width + Height);
            public IReadOnlyList<DbPoint> Corners { get; }

            public MinimumAreaRectangle Expand(double distance)
            {
                if (double.IsNaN(distance) || double.IsInfinity(distance) || distance < 0.0)
                    throw new ArgumentOutOfRangeException(nameof(distance));
                return new MinimumAreaRectangle(
                    CenterX,
                    CenterY,
                    Angle,
                    Width + (2.0 * distance),
                    Height + (2.0 * distance));
            }

            public static MinimumAreaRectangle FromHull(IReadOnlyList<DbPoint> hull)
            {
                if (hull == null) throw new ArgumentNullException(nameof(hull));
                if (hull.Count < 3) throw new ArgumentException("At least three hull points are required.", nameof(hull));

                var bestArea = double.PositiveInfinity;
                var bestAngle = 0.0;
                var bestMinX = 0.0;
                var bestMaxX = 0.0;
                var bestMinY = 0.0;
                var bestMaxY = 0.0;

                for (var edgeIndex = 0; edgeIndex < hull.Count; edgeIndex++)
                {
                    var a = hull[edgeIndex];
                    var b = hull[(edgeIndex + 1) % hull.Count];
                    var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
                    var cos = Math.Cos(angle);
                    var sin = Math.Sin(angle);

                    var minX = double.PositiveInfinity;
                    var maxX = double.NegativeInfinity;
                    var minY = double.PositiveInfinity;
                    var maxY = double.NegativeInfinity;
                    for (var pointIndex = 0; pointIndex < hull.Count; pointIndex++)
                    {
                        var point = hull[pointIndex];
                        var rotatedX = (point.X * cos) + (point.Y * sin);
                        var rotatedY = (-point.X * sin) + (point.Y * cos);
                        minX = Math.Min(minX, rotatedX);
                        maxX = Math.Max(maxX, rotatedX);
                        minY = Math.Min(minY, rotatedY);
                        maxY = Math.Max(maxY, rotatedY);
                    }

                    var area = (maxX - minX) * (maxY - minY);
                    if (area >= bestArea - 1e-12)
                        continue;

                    bestArea = area;
                    bestAngle = angle;
                    bestMinX = minX;
                    bestMaxX = maxX;
                    bestMinY = minY;
                    bestMaxY = maxY;
                }

                var bestCos = Math.Cos(bestAngle);
                var bestSin = Math.Sin(bestAngle);
                var centerRotatedX = (bestMinX + bestMaxX) * 0.5;
                var centerRotatedY = (bestMinY + bestMaxY) * 0.5;
                var centerX = (centerRotatedX * bestCos) - (centerRotatedY * bestSin);
                var centerY = (centerRotatedX * bestSin) + (centerRotatedY * bestCos);

                return new MinimumAreaRectangle(
                    centerX,
                    centerY,
                    bestAngle,
                    bestMaxX - bestMinX,
                    bestMaxY - bestMinY);
            }

            private static List<DbPoint> CreateCorners(
                double centerX,
                double centerY,
                double angle,
                double width,
                double height)
            {
                var halfWidth = width * 0.5;
                var halfHeight = height * 0.5;
                var cos = Math.Cos(angle);
                var sin = Math.Sin(angle);
                var local = new[]
                {
                    new DbPoint(-halfWidth, -halfHeight),
                    new DbPoint(halfWidth, -halfHeight),
                    new DbPoint(halfWidth, halfHeight),
                    new DbPoint(-halfWidth, halfHeight)
                };

                var result = new List<DbPoint>(4);
                foreach (var point in local)
                {
                    result.Add(new DbPoint(
                        centerX + (point.X * cos) - (point.Y * sin),
                        centerY + (point.X * sin) + (point.Y * cos)));
                }
                return result;
            }

            private static IReadOnlyList<DbPoint> OrderLikePaddle(IReadOnlyList<DbPoint> corners)
            {
                var points = corners.OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
                var index1 = points[1].Y > points[0].Y ? 0 : 1;
                var index4 = points[1].Y > points[0].Y ? 1 : 0;
                var index2 = points[3].Y > points[2].Y ? 2 : 3;
                var index3 = points[3].Y > points[2].Y ? 3 : 2;
                return new[] { points[index1], points[index2], points[index3], points[index4] };
            }
        }
    }
}
