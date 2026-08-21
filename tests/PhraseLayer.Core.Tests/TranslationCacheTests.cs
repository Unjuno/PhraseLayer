using System;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class TranslationCacheTests
    {
        [Fact]
        public async Task RepeatedExactRequestHitsCache()
        {
            var inner = new RecordingTranslationEngine();
            var cache = new CachingTranslationEngine(inner, maxEntries: 4);

            var first = await cache.TranslateAsync("keep off", "Please keep off the grass.");
            var second = await cache.TranslateAsync("keep off", "Please keep off the grass.");

            Assert.Equal(first, second);
            Assert.Equal(1, inner.CallCount);
            Assert.Equal(1, cache.Hits);
            Assert.Equal(1, cache.Misses);
            Assert.Equal(1, cache.Count);
        }

        [Fact]
        public async Task DifferentContextDoesNotReusePotentiallyWrongTranslation()
        {
            var inner = new RecordingTranslationEngine();
            var cache = new CachingTranslationEngine(inner, maxEntries: 4);

            await cache.TranslateAsync("bank", "I sat on the river bank.");
            await cache.TranslateAsync("bank", "I went to the bank.");

            Assert.Equal(2, inner.CallCount);
            Assert.Equal(0, cache.Hits);
            Assert.Equal(2, cache.Misses);
            Assert.Equal(2, cache.Count);
        }

        [Fact]
        public async Task CacheUsesExactSourceIdentityInsteadOfCaseFolding()
        {
            var inner = new RecordingTranslationEngine();
            var cache = new CachingTranslationEngine(inner, maxEntries: 4);

            await cache.TranslateAsync("US", "US policy");
            await cache.TranslateAsync("us", "US policy");

            Assert.Equal(2, inner.CallCount);
            Assert.Equal(2, cache.Count);
        }

        [Fact]
        public async Task LeastRecentlyUsedEntryIsEvictedAtCapacity()
        {
            var inner = new RecordingTranslationEngine();
            var cache = new CachingTranslationEngine(inner, maxEntries: 2);

            await cache.TranslateAsync("A", "context"); // miss A
            await cache.TranslateAsync("B", "context"); // miss B
            await cache.TranslateAsync("A", "context"); // hit A => B is LRU
            await cache.TranslateAsync("C", "context"); // miss C => evict B
            await cache.TranslateAsync("B", "context"); // miss B again

            Assert.Equal(4, inner.CallCount);
            Assert.Equal(1, cache.Hits);
            Assert.Equal(4, cache.Misses);
            Assert.Equal(2, cache.Count);
        }

        [Fact]
        public async Task FailedTranslationIsNeverCached()
        {
            var inner = new FailOnceTranslationEngine();
            var cache = new CachingTranslationEngine(inner, maxEntries: 4);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => cache.TranslateAsync("keep off", "context"));
            var second = await cache.TranslateAsync("keep off", "context");

            Assert.Equal("keep off|context", second);
            Assert.Equal(2, inner.CallCount);
            Assert.Equal(0, cache.Hits);
            Assert.Equal(2, cache.Misses);
            Assert.Equal(1, cache.Count);
        }

        [Fact]
        public async Task PreCancelledRequestNeverCallsInnerEngine()
        {
            var inner = new RecordingTranslationEngine();
            var cache = new CachingTranslationEngine(inner, maxEntries: 4);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cache.TranslateAsync("keep off", "context", cancellation.Token));

            Assert.Equal(0, inner.CallCount);
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.Hits);
            Assert.Equal(0, cache.Misses);
        }

        [Fact]
        public void InvalidCapacityIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new CachingTranslationEngine(new RecordingTranslationEngine(), 0));
        }

        private sealed class RecordingTranslationEngine : ITranslationEngine
        {
            public int CallCount { get; private set; }

            public Task<string> TranslateAsync(
                string sourceText,
                string context,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                return Task.FromResult(sourceText + "|" + context);
            }
        }

        private sealed class FailOnceTranslationEngine : ITranslationEngine
        {
            public int CallCount { get; private set; }

            public Task<string> TranslateAsync(
                string sourceText,
                string context,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                if (CallCount == 1)
                    throw new InvalidOperationException("synthetic translation failure");
                return Task.FromResult(sourceText + "|" + context);
            }
        }
    }
}
