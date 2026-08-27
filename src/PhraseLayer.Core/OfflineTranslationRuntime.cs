using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PhraseLayer.Core.Translation
{
    public sealed class OfflineTranslationRequest
    {
        public OfflineTranslationRequest(string sourceText, string context)
        {
            SourceText = sourceText ?? throw new ArgumentNullException(nameof(sourceText));
            Context = context ?? string.Empty;
        }

        public string SourceText { get; }
        public string Context { get; }
    }

    public enum TranslationGenerationStopReason
    {
        EndOfSequence = 0,
        MaximumTokens = 1
    }

    public sealed class TranslationTokenSequence
    {
        public TranslationTokenSequence(IReadOnlyList<int> tokenIds, bool wasTruncated)
        {
            TokenIds = tokenIds ?? throw new ArgumentNullException(nameof(tokenIds));
            if (tokenIds.Count == 0)
                throw new ArgumentException("A translation token sequence must contain at least one token.", nameof(tokenIds));
            TokenIds = tokenIds.ToArray();
            WasTruncated = wasTruncated;
        }

        public IReadOnlyList<int> TokenIds { get; }
        public bool WasTruncated { get; }
    }

    public sealed class TranslationGenerationResult
    {
        public TranslationGenerationResult(
            IReadOnlyList<int> tokenIds,
            TranslationGenerationStopReason stopReason)
        {
            TokenIds = tokenIds ?? throw new ArgumentNullException(nameof(tokenIds));
            if (tokenIds.Count == 0)
                throw new ArgumentException("Generated translation tokens must not be empty.", nameof(tokenIds));
            TokenIds = tokenIds.ToArray();
            StopReason = stopReason;
        }

        public IReadOnlyList<int> TokenIds { get; }
        public TranslationGenerationStopReason StopReason { get; }
    }

    public sealed class TranslationGenerationOptions
    {
        public TranslationGenerationOptions(
            int maximumSourceTokens = 128,
            int maximumTargetTokens = 128,
            int beamWidth = 1)
        {
            if (maximumSourceTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSourceTokens));
            if (maximumTargetTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maximumTargetTokens));
            if (beamWidth <= 0) throw new ArgumentOutOfRangeException(nameof(beamWidth));
            MaximumSourceTokens = maximumSourceTokens;
            MaximumTargetTokens = maximumTargetTokens;
            BeamWidth = beamWidth;
        }

        public int MaximumSourceTokens { get; }
        public int MaximumTargetTokens { get; }
        public int BeamWidth { get; }
    }

    public sealed class OfflineTranslationResult
    {
        public OfflineTranslationResult(
            string translatedText,
            int sourceTokenCount,
            int generatedTokenCount,
            bool sourceWasTruncated,
            TranslationGenerationStopReason stopReason)
        {
            if (translatedText == null) throw new ArgumentNullException(nameof(translatedText));
            if (sourceTokenCount <= 0) throw new ArgumentOutOfRangeException(nameof(sourceTokenCount));
            if (generatedTokenCount <= 0) throw new ArgumentOutOfRangeException(nameof(generatedTokenCount));

            TranslatedText = translatedText;
            SourceTokenCount = sourceTokenCount;
            GeneratedTokenCount = generatedTokenCount;
            SourceWasTruncated = sourceWasTruncated;
            StopReason = stopReason;
        }

        public string TranslatedText { get; }
        public int SourceTokenCount { get; }
        public int GeneratedTokenCount { get; }
        public bool SourceWasTruncated { get; }
        public TranslationGenerationStopReason StopReason { get; }
    }

    public interface ITranslationTokenizer
    {
        TranslationTokenSequence EncodeSource(string sourceText, int maximumTokens);
        string DecodeTarget(IReadOnlyList<int> targetTokenIds);
    }

    public interface ISeq2SeqTranslationModel
    {
        Task<TranslationGenerationResult> GenerateAsync(
            IReadOnlyList<int> sourceTokenIds,
            TranslationGenerationOptions options,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    public interface IOfflineTranslationRuntime
    {
        Task<OfflineTranslationResult> TranslateAsync(
            OfflineTranslationRequest request,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>
    /// Platform-neutral orchestration for an offline encoder/decoder translation stack.
    /// Tokenization and model execution remain replaceable so Unity/Quest code can provide reviewed
    /// SentencePiece and Unity Inference implementations without leaking runtime dependencies into Core.
    /// Context is preserved on the request contract for future context-aware runtimes; this baseline
    /// intentionally translates SourceText itself rather than silently changing the requested semantic span.
    /// </summary>
    public sealed class OfflineSeq2SeqTranslationRuntime : IOfflineTranslationRuntime
    {
        private readonly ITranslationTokenizer tokenizer;
        private readonly ISeq2SeqTranslationModel model;
        private readonly TranslationGenerationOptions options;

        public OfflineSeq2SeqTranslationRuntime(
            ITranslationTokenizer tokenizer,
            ISeq2SeqTranslationModel model,
            TranslationGenerationOptions? options = null)
        {
            this.tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
            this.model = model ?? throw new ArgumentNullException(nameof(model));
            this.options = options ?? new TranslationGenerationOptions();
        }

        public TranslationGenerationOptions Options => options;

        public async Task<OfflineTranslationResult> TranslateAsync(
            OfflineTranslationRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(request.SourceText))
                throw new ArgumentException("Offline translation source text must not be empty.", nameof(request));

            var source = tokenizer.EncodeSource(request.SourceText, options.MaximumSourceTokens);
            cancellationToken.ThrowIfCancellationRequested();

            var generated = await model.GenerateAsync(source.TokenIds, options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generated == null)
                throw new InvalidOperationException("Offline translation model returned no generation result.");

            var translated = tokenizer.DecodeTarget(generated.TokenIds);
            if (string.IsNullOrWhiteSpace(translated))
                throw new InvalidOperationException("Offline translation tokenizer decoded an empty translation.");

            return new OfflineTranslationResult(
                translated,
                source.TokenIds.Count,
                generated.TokenIds.Count,
                source.WasTruncated,
                generated.StopReason);
        }
    }

    /// <summary>
    /// ITranslationEngine adapter used by LanguagePipeline. It keeps the existing public translation boundary
    /// stable while allowing the implementation underneath it to become fully offline and model-backed.
    /// </summary>
    public sealed class OfflineTranslationEngine : ITranslationEngine
    {
        private readonly IOfflineTranslationRuntime runtime;

        public OfflineTranslationEngine(IOfflineTranslationRuntime runtime)
        {
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        public async Task<string> TranslateAsync(
            string sourceText,
            string context,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (sourceText == null) throw new ArgumentNullException(nameof(sourceText));
            if (context == null) throw new ArgumentNullException(nameof(context));
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceText.Length == 0) return string.Empty;

            var result = await runtime.TranslateAsync(
                new OfflineTranslationRequest(sourceText, context),
                cancellationToken);
            if (result == null)
                throw new InvalidOperationException("Offline translation runtime returned no result.");
            return result.TranslatedText;
        }
    }
}
