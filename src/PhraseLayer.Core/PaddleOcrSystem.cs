using System;
using System.Collections.Generic;
using System.Linq;

namespace PhraseLayer.Core.Inputs
{
    public sealed class PaddleOcrRecognizedCandidate
    {
        public PaddleOcrRecognizedCandidate(
            string text,
            double recognitionConfidence,
            double detectionScore,
            ImageQuad imageBounds)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            ValidateUnitInterval(recognitionConfidence, nameof(recognitionConfidence));
            ValidateUnitInterval(detectionScore, nameof(detectionScore));
            RecognitionConfidence = recognitionConfidence;
            DetectionScore = detectionScore;
            ImageBounds = imageBounds;
        }

        public string Text { get; }
        public double RecognitionConfidence { get; }
        public double DetectionScore { get; }
        public ImageQuad ImageBounds { get; }

        private static void ValidateUnitInterval(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    /// <summary>
    /// Reading-order mirror of PaddleOCR tools/infer/predict_system.py sorted_boxes.
    /// The detector's p0 point is used as the ordering key. After top-to-bottom/y then left-to-right/x
    /// sorting, neighboring boxes whose p0 y values differ by less than 10 pixels are swapped leftward
    /// until each local text line is left-to-right.
    /// </summary>
    public static class PaddleOcrReadingOrder
    {
        public const double SameLineTolerancePixels = 10.0;

        public static IReadOnlyList<PaddleDbQuadDetection> Sort(
            IReadOnlyList<PaddleDbQuadDetection> detections)
        {
            if (detections == null) throw new ArgumentNullException(nameof(detections));

            var ordered = detections
                .Select((detection, index) => new IndexedDetection(
                    detection ?? throw new ArgumentException(
                        "Detection list cannot contain null entries.", nameof(detections)),
                    index))
                .OrderBy(item => item.Detection.ImageBounds.P0.Y)
                .ThenBy(item => item.Detection.ImageBounds.P0.X)
                .ThenBy(item => item.OriginalIndex)
                .Select(item => item.Detection)
                .ToList();

            for (var i = 0; i < ordered.Count - 1; i++)
            {
                for (var j = i; j >= 0; j--)
                {
                    var current = ordered[j];
                    var next = ordered[j + 1];
                    var currentPoint = current.ImageBounds.P0;
                    var nextPoint = next.ImageBounds.P0;
                    if (Math.Abs(nextPoint.Y - currentPoint.Y) < SameLineTolerancePixels &&
                        nextPoint.X < currentPoint.X)
                    {
                        ordered[j] = next;
                        ordered[j + 1] = current;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return ordered;
        }

        private readonly struct IndexedDetection
        {
            public IndexedDetection(PaddleDbQuadDetection detection, int originalIndex)
            {
                Detection = detection;
                OriginalIndex = originalIndex;
            }

            public PaddleDbQuadDetection Detection { get; }
            public int OriginalIndex { get; }
        }
    }

    /// <summary>
    /// Converts per-box recognition results into the Core OCR contract.
    /// PaddleOCR's default recognition drop score is 0.5 and acceptance is inclusive (score >= threshold).
    /// Region confidence is the recognition confidence; detector score is retained on the candidate but is not
    /// multiplied into the OCR confidence because no independence/calibration assumption is justified.
    /// Observation confidence is the arithmetic mean of retained recognition confidences.
    /// </summary>
    public static class PaddleOcrObservationAssembler
    {
        public const double DefaultRecognitionDropScore = 0.5;

        public static OcrObservation Assemble(
            IReadOnlyList<PaddleOcrRecognizedCandidate> candidates,
            double recognitionDropScore = DefaultRecognitionDropScore,
            string textSeparator = "\n")
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (textSeparator == null) throw new ArgumentNullException(nameof(textSeparator));
            if (double.IsNaN(recognitionDropScore) || double.IsInfinity(recognitionDropScore) ||
                recognitionDropScore < 0.0 || recognitionDropScore > 1.0)
            {
                throw new ArgumentOutOfRangeException(nameof(recognitionDropScore));
            }

            var regions = new List<OcrRegion>();
            var texts = new List<string>();
            double confidenceSum = 0.0;

            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index] ?? throw new ArgumentException(
                    "Candidate list cannot contain null entries.", nameof(candidates));
                if (candidate.RecognitionConfidence < recognitionDropScore)
                    continue;

                regions.Add(new OcrRegion(
                    candidate.Text,
                    candidate.RecognitionConfidence,
                    candidate.ImageBounds));
                texts.Add(candidate.Text);
                confidenceSum += candidate.RecognitionConfidence;
            }

            var confidence = regions.Count == 0 ? 0.0 : confidenceSum / regions.Count;
            return new OcrObservation(
                string.Join(textSeparator, texts),
                confidence,
                regions);
        }
    }
}
