using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Learning;

namespace PhraseLayer.Core.Translation
{
    public interface ITranslationEngine
    {
        Task<string> TranslateAsync(string sourceText, string context, CancellationToken cancellationToken = default(CancellationToken));
    }

    public sealed class DictionaryTranslationEngine : ITranslationEngine
    {
        private readonly IReadOnlyDictionary<string, string> _translations;
        public DictionaryTranslationEngine(IReadOnlyDictionary<string, string> translations) { _translations = translations ?? throw new ArgumentNullException(nameof(translations)); }
        public Task<string> TranslateAsync(string sourceText, string context, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string translation;
            return Task.FromResult(_translations.TryGetValue(InMemoryLearnerModel.Normalize(sourceText), out translation) ? translation : sourceText);
        }
    }

    /// <summary>
    /// Bounded in-memory LRU cache for local translation engines.
    ///
    /// Cache identity deliberately includes the exact source span and exact context. A contextual NMT engine may
    /// legitimately translate the same source phrase differently in a different sentence, so source-only caching
    /// would be a correctness bug. Failed or cancelled requests are never cached.
    /// </summary>
    public sealed class CachingTranslationEngine : ITranslationEngine
    {
        private readonly ITranslationEngine inner;
        private readonly int maxEntries;
        private readonly object gate = new object();
        private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> entries =
            new Dictionary<CacheKey, LinkedListNode<CacheEntry>>();
        private readonly LinkedList<CacheEntry> lru = new LinkedList<CacheEntry>();
        private long hits;
        private long misses;

        public CachingTranslationEngine(ITranslationEngine inner, int maxEntries = 512)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
            this.maxEntries = maxEntries;
        }

        public int MaxEntries => maxEntries;

        public int Count
        {
            get
            {
                lock (gate) return entries.Count;
            }
        }

        public long Hits
        {
            get
            {
                lock (gate) return hits;
            }
        }

        public long Misses
        {
            get
            {
                lock (gate) return misses;
            }
        }

        public async Task<string> TranslateAsync(
            string sourceText,
            string context,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (sourceText == null) throw new ArgumentNullException(nameof(sourceText));
            if (context == null) throw new ArgumentNullException(nameof(context));
            cancellationToken.ThrowIfCancellationRequested();

            var key = new CacheKey(sourceText, context);
            string? cached = null;
            lock (gate)
            {
                LinkedListNode<CacheEntry>? node;
                if (entries.TryGetValue(key, out node))
                {
                    hits++;
                    lru.Remove(node);
                    lru.AddFirst(node);
                    cached = node.Value.Translation;
                }
                else
                {
                    misses++;
                }
            }

            if (cached != null) return cached;

            // Do not hold the cache lock across inference. The wrapped engine may be main-thread bound (Unity
            // Inference) or expensive. Duplicate simultaneous misses are acceptable; correctness and cancellation
            // isolation are more important than cross-caller request coalescing.
            var translated = await inner
                .TranslateAsync(sourceText, context, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (translated == null)
                throw new InvalidOperationException("Translation engine returned null.");

            lock (gate)
            {
                LinkedListNode<CacheEntry>? existing;
                if (entries.TryGetValue(key, out existing))
                {
                    // Another caller may have populated the same key while this request was in flight. Keep the
                    // newest successful result and move it to the MRU position.
                    lru.Remove(existing);
                    entries.Remove(key);
                }

                var created = new LinkedListNode<CacheEntry>(new CacheEntry(key, translated));
                lru.AddFirst(created);
                entries.Add(key, created);
                TrimToCapacity();
            }

            return translated;
        }

        public void Clear()
        {
            lock (gate)
            {
                entries.Clear();
                lru.Clear();
            }
        }

        private void TrimToCapacity()
        {
            while (entries.Count > maxEntries)
            {
                var last = lru.Last;
                if (last == null)
                    throw new InvalidOperationException("Translation cache LRU list became inconsistent.");
                lru.RemoveLast();
                entries.Remove(last.Value.Key);
            }
        }

        private readonly struct CacheKey : IEquatable<CacheKey>
        {
            public CacheKey(string sourceText, string context)
            {
                SourceText = sourceText;
                Context = context;
            }

            public string SourceText { get; }
            public string Context { get; }

            public bool Equals(CacheKey other)
            {
                return string.Equals(SourceText, other.SourceText, StringComparison.Ordinal) &&
                       string.Equals(Context, other.Context, StringComparison.Ordinal);
            }

            public override bool Equals(object? obj)
            {
                return obj is CacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (StringComparer.Ordinal.GetHashCode(SourceText) * 397) ^
                           StringComparer.Ordinal.GetHashCode(Context);
                }
            }
        }

        private readonly struct CacheEntry
        {
            public CacheEntry(CacheKey key, string translation)
            {
                Key = key;
                Translation = translation;
            }

            public CacheKey Key { get; }
            public string Translation { get; }
        }
    }
}
