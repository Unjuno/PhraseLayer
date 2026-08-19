using System;
using System.Collections.Generic;

namespace PhraseLayer.Core.Diagnostics
{
    /// <summary>
    /// Host-testable accumulator for the Gate 4 measurements that can be sampled on Quest.
    /// It deliberately stores raw units (microseconds/bytes) and does not manufacture missing device data.
    /// </summary>
    public sealed class OcrBenchmarkAccumulator
    {
        private readonly int capacity;
        private readonly Queue<long> inferenceMicroseconds;
        private readonly Queue<long> xrFrameMicroseconds;
        private long? coldStartMicroseconds;
        private long? peakPssBytes;
        private long? steadyPssBytes;

        public OcrBenchmarkAccumulator(int capacity = 512)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
            inferenceMicroseconds = new Queue<long>(capacity);
            xrFrameMicroseconds = new Queue<long>(capacity);
        }

        public void RecordColdStart(long elapsedMicroseconds)
        {
            ValidateNonNegative(elapsedMicroseconds, nameof(elapsedMicroseconds));
            coldStartMicroseconds = elapsedMicroseconds;
        }

        public void RecordInference(long elapsedMicroseconds)
        {
            ValidateNonNegative(elapsedMicroseconds, nameof(elapsedMicroseconds));
            EnqueueBounded(inferenceMicroseconds, elapsedMicroseconds);
        }

        public void RecordXrFrameTime(long elapsedMicroseconds)
        {
            ValidateNonNegative(elapsedMicroseconds, nameof(elapsedMicroseconds));
            EnqueueBounded(xrFrameMicroseconds, elapsedMicroseconds);
        }

        public void RecordMemoryPss(long steadyBytes, long peakBytes)
        {
            ValidateNonNegative(steadyBytes, nameof(steadyBytes));
            ValidateNonNegative(peakBytes, nameof(peakBytes));
            if (peakBytes < steadyBytes)
                throw new ArgumentOutOfRangeException(nameof(peakBytes), "Peak PSS cannot be lower than steady PSS for the same snapshot.");

            steadyPssBytes = steadyBytes;
            peakPssBytes = peakPssBytes.HasValue
                ? Math.Max(peakPssBytes.Value, peakBytes)
                : peakBytes;
        }

        public OcrBenchmarkSnapshot Snapshot()
        {
            return new OcrBenchmarkSnapshot(
                coldStartMicroseconds,
                BuildDistribution(inferenceMicroseconds),
                steadyPssBytes,
                peakPssBytes,
                BuildDistribution(xrFrameMicroseconds));
        }

        public void Reset()
        {
            inferenceMicroseconds.Clear();
            xrFrameMicroseconds.Clear();
            coldStartMicroseconds = null;
            peakPssBytes = null;
            steadyPssBytes = null;
        }

        private void EnqueueBounded(Queue<long> queue, long value)
        {
            if (queue.Count == capacity) queue.Dequeue();
            queue.Enqueue(value);
        }

        private static LatencyDistribution BuildDistribution(Queue<long> values)
        {
            if (values.Count == 0) return LatencyDistribution.Empty;

            var sorted = values.ToArray();
            Array.Sort(sorted);
            return new LatencyDistribution(
                sorted.Length,
                PercentileNearestRank(sorted, 0.50),
                PercentileNearestRank(sorted, 0.95));
        }

        private static long PercentileNearestRank(long[] sortedValues, double probability)
        {
            var rank = (int)Math.Ceiling(probability * sortedValues.Length);
            var index = Math.Max(0, Math.Min(sortedValues.Length - 1, rank - 1));
            return sortedValues[index];
        }

        private static void ValidateNonNegative(long value, string parameterName)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    public readonly struct LatencyDistribution
    {
        public static readonly LatencyDistribution Empty = new LatencyDistribution(0, null, null);

        public LatencyDistribution(int sampleCount, long? p50Microseconds, long? p95Microseconds)
        {
            if (sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
            SampleCount = sampleCount;
            P50Microseconds = p50Microseconds;
            P95Microseconds = p95Microseconds;
        }

        public int SampleCount { get; }
        public long? P50Microseconds { get; }
        public long? P95Microseconds { get; }
    }

    public readonly struct OcrBenchmarkSnapshot
    {
        public OcrBenchmarkSnapshot(
            long? coldStartMicroseconds,
            LatencyDistribution inferenceLatency,
            long? steadyPssBytes,
            long? peakPssBytes,
            LatencyDistribution xrFrameTime)
        {
            ColdStartMicroseconds = coldStartMicroseconds;
            InferenceLatency = inferenceLatency;
            SteadyPssBytes = steadyPssBytes;
            PeakPssBytes = peakPssBytes;
            XrFrameTime = xrFrameTime;
        }

        public long? ColdStartMicroseconds { get; }
        public LatencyDistribution InferenceLatency { get; }
        public long? SteadyPssBytes { get; }
        public long? PeakPssBytes { get; }
        public LatencyDistribution XrFrameTime { get; }
    }
}
