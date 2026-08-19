using System;
using PhraseLayer.Core.Diagnostics;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class OcrBenchmarkingTests
    {
        [Fact]
        public void SnapshotUsesNearestRankPercentiles()
        {
            var benchmark = new OcrBenchmarkAccumulator();
            foreach (var value in new long[] { 1000, 2000, 3000, 4000, 5000 })
                benchmark.RecordInference(value);

            var snapshot = benchmark.Snapshot();

            Assert.Equal(5, snapshot.InferenceLatency.SampleCount);
            Assert.Equal(3000, snapshot.InferenceLatency.P50Microseconds);
            Assert.Equal(5000, snapshot.InferenceLatency.P95Microseconds);
        }

        [Fact]
        public void CapacityKeepsNewestSamplesOnly()
        {
            var benchmark = new OcrBenchmarkAccumulator(capacity: 3);
            benchmark.RecordInference(1000);
            benchmark.RecordInference(2000);
            benchmark.RecordInference(3000);
            benchmark.RecordInference(9000);

            var snapshot = benchmark.Snapshot();

            Assert.Equal(3, snapshot.InferenceLatency.SampleCount);
            Assert.Equal(3000, snapshot.InferenceLatency.P50Microseconds);
            Assert.Equal(9000, snapshot.InferenceLatency.P95Microseconds);
        }

        [Fact]
        public void MemoryTracksLatestSteadyAndMaximumPeak()
        {
            var benchmark = new OcrBenchmarkAccumulator();
            benchmark.RecordMemoryPss(100, 150);
            benchmark.RecordMemoryPss(120, 140);

            var snapshot = benchmark.Snapshot();

            Assert.Equal(120, snapshot.SteadyPssBytes);
            Assert.Equal(150, snapshot.PeakPssBytes);
        }

        [Fact]
        public void ColdStartAndXrFrameTimeAreIndependentMeasurements()
        {
            var benchmark = new OcrBenchmarkAccumulator();
            benchmark.RecordColdStart(250000);
            benchmark.RecordXrFrameTime(11000);
            benchmark.RecordXrFrameTime(13000);

            var snapshot = benchmark.Snapshot();

            Assert.Equal(250000, snapshot.ColdStartMicroseconds);
            Assert.Equal(2, snapshot.XrFrameTime.SampleCount);
            Assert.Equal(11000, snapshot.XrFrameTime.P50Microseconds);
            Assert.Equal(13000, snapshot.XrFrameTime.P95Microseconds);
            Assert.Equal(0, snapshot.InferenceLatency.SampleCount);
        }

        [Fact]
        public void ResetRemovesAllMeasurements()
        {
            var benchmark = new OcrBenchmarkAccumulator();
            benchmark.RecordColdStart(100);
            benchmark.RecordInference(200);
            benchmark.RecordXrFrameTime(300);
            benchmark.RecordMemoryPss(400, 500);

            benchmark.Reset();
            var snapshot = benchmark.Snapshot();

            Assert.Null(snapshot.ColdStartMicroseconds);
            Assert.Null(snapshot.SteadyPssBytes);
            Assert.Null(snapshot.PeakPssBytes);
            Assert.Equal(0, snapshot.InferenceLatency.SampleCount);
            Assert.Equal(0, snapshot.XrFrameTime.SampleCount);
        }

        [Fact]
        public void InvalidMeasurementsAreRejected()
        {
            var benchmark = new OcrBenchmarkAccumulator();

            Assert.Throws<ArgumentOutOfRangeException>(() => benchmark.RecordColdStart(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => benchmark.RecordInference(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => benchmark.RecordXrFrameTime(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => benchmark.RecordMemoryPss(100, 99));
        }
    }
}
