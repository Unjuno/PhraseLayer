using System;
using System.Collections.Generic;

namespace PhraseLayer.Core.Translation
{
    /// <summary>
    /// Marker/wrapper proving that a tokenizer instance passed a concrete token-exact fixture set.
    /// Construction is intentionally restricted to Verify so official runtime factories cannot accidentally
    /// substitute an unverified tokenizer implementation.
    /// </summary>
    public sealed class ParityVerifiedTranslationTokenizer : ITranslationTokenizer
    {
        private readonly ITranslationTokenizer inner;

        private ParityVerifiedTranslationTokenizer(ITranslationTokenizer inner, string parityReport)
        {
            this.inner = inner;
            ParityReport = parityReport;
        }

        public string ParityReport { get; }

        public static ParityVerifiedTranslationTokenizer Verify(
            ITranslationTokenizer tokenizer,
            TranslationTokenizerFixtureSet fixtures)
        {
            if (tokenizer == null) throw new ArgumentNullException(nameof(tokenizer));
            if (fixtures == null) throw new ArgumentNullException(nameof(fixtures));
            var report = TranslationTokenizerParityGate.ValidateAndBuildReport(tokenizer, fixtures);
            return new ParityVerifiedTranslationTokenizer(tokenizer, report);
        }

        public IReadOnlyList<int> Encode(string text)
        {
            return inner.Encode(text);
        }

        public string Decode(IReadOnlyList<int> tokenIds)
        {
            return inner.Decode(tokenIds);
        }
    }

    /// <summary>
    /// Official local OPUS-MT reference engine construction path.
    /// Tokenizer parity is established before the model-backed engine is exposed, and successful translations
    /// are then protected by the bounded context-aware cache. No network fallback exists.
    /// </summary>
    public static class OpusMtEnJapLocalEngineFactory
    {
        public const int DefaultCacheEntries = 512;

        public static ITranslationEngine CreateReferenceEngine(
            ITranslationTokenizer tokenizer,
            TranslationTokenizerFixtureSet fixtures,
            IAutoregressiveTranslationBackend backend,
            int cacheEntries = DefaultCacheEntries)
        {
            if (backend == null) throw new ArgumentNullException(nameof(backend));
            if (cacheEntries <= 0) throw new ArgumentOutOfRangeException(nameof(cacheEntries));

            var verifiedTokenizer = ParityVerifiedTranslationTokenizer.Verify(tokenizer, fixtures);
            var engine = OpusMtEnJapGenerationContract.CreateReferenceEngine(verifiedTokenizer, backend);
            return new CachingTranslationEngine(engine, cacheEntries);
        }
    }
}
