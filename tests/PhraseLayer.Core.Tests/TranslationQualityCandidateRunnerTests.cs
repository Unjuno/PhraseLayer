using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class TranslationQualityCandidateRunnerTests
    {
        [Fact]
        public async Task RunnerPreservesOrderAndUsesSourceAsContext()
        {
            var cases = new[]
            {
                Case("a", "Do not enter."),
                Case("b", "Please keep off the grass."),
            };
            var engine = new RecordingTranslationEngine();

            var output = await TranslationQualityCandidateRunner.RunAsync(cases, engine);

            Assert.Equal(2, output.Count);
            Assert.Equal("a", output[0].CaseId);
            Assert.Equal("候補:Do not enter.", output[0].CandidateText);
            Assert.Equal("b", output[1].CaseId);
            Assert.Equal("候補:Please keep off the grass.", output[1].CandidateText);
            Assert.Equal(
                new[]
                {
                    ("Do not enter.", "Do not enter."),
                    ("Please keep off the grass.", "Please keep off the grass."),
                },
                engine.Calls);
        }

        [Fact]
        public async Task RunnerStopsBeforeNextCaseWhenTranslationFails()
        {
            var cases = new[]
            {
                Case("a", "first"),
                Case("b", "second"),
                Case("c", "third"),
            };
            var engine = new ThrowingTranslationEngine("second");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TranslationQualityCandidateRunner.RunAsync(cases, engine));

            Assert.Equal(new[] { "first", "second" }, engine.Calls);
        }

        [Fact]
        public async Task RunnerHonorsCancellationBetweenCases()
        {
            var cases = new[]
            {
                Case("a", "first"),
                Case("b", "second"),
            };
            using var cancellation = new CancellationTokenSource();
            var engine = new CancellingTranslationEngine(cancellation);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                TranslationQualityCandidateRunner.RunAsync(cases, engine, cancellation.Token));

            Assert.Equal(1, engine.CallCount);
        }

        [Fact]
        public async Task DuplicateCaseIdsAreRejectedBeforeDuplicateIsTranslated()
        {
            var cases = new[]
            {
                Case("a", "first"),
                Case("a", "second"),
            };
            var engine = new RecordingTranslationEngine();

            await Assert.ThrowsAsync<ArgumentException>(() =>
                TranslationQualityCandidateRunner.RunAsync(cases, engine));

            Assert.Single(engine.Calls);
        }

        private static TranslationQualityCase Case(string id, string source)
        {
            return new TranslationQualityCase(
                id,
                source,
                new[]
                {
                    TranslationQualityDimension.Adequacy,
                    TranslationQualityDimension.JapaneseReadability,
                },
                "candidate runner fixture");
        }

        private sealed class RecordingTranslationEngine : ITranslationEngine
        {
            public List<(string Source, string Context)> Calls { get; } =
                new List<(string Source, string Context)>();

            public Task<string> TranslateAsync(
                string sourceText,
                string context,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls.Add((sourceText, context));
                return Task.FromResult("候補:" + sourceText);
            }
        }

        private sealed class ThrowingTranslationEngine : ITranslationEngine
        {
            private readonly string failingSource;

            public ThrowingTranslationEngine(string failingSource)
            {
                this.failingSource = failingSource;
            }

            public List<string> Calls { get; } = new List<string>();

            public Task<string> TranslateAsync(
                string sourceText,
                string context,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                Calls.Add(sourceText);
                if (string.Equals(sourceText, failingSource, StringComparison.Ordinal))
                    throw new InvalidOperationException("synthetic translation failure");
                return Task.FromResult(sourceText);
            }
        }

        private sealed class CancellingTranslationEngine : ITranslationEngine
        {
            private readonly CancellationTokenSource cancellation;

            public CancellingTranslationEngine(CancellationTokenSource cancellation)
            {
                this.cancellation = cancellation;
            }

            public int CallCount { get; private set; }

            public Task<string> TranslateAsync(
                string sourceText,
                string context,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                CallCount++;
                cancellation.Cancel();
                return Task.FromResult(sourceText);
            }
        }
    }
}
