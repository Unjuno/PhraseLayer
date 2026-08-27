using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class OfflineTranslationRuntimeTests
    {
        [Fact]
        public async Task Seq2SeqRuntimeTranslatesExactRequestedSpanAndPreservesDiagnostics()
        {
            var tokenizer = new RecordingTokenizer("立ち入らない");
            var model = new RecordingModel(
                new TranslationGenerationResult(
                    new[] { 91, 92, 0 },
                    TranslationGenerationStopReason.EndOfSequence));
            var options = new TranslationGenerationOptions(64, 48, 2);
            var runtime = new OfflineSeq2SeqTranslationRuntime(tokenizer, model, options);

            var result = await runtime.TranslateAsync(
                new OfflineTranslationRequest(
                    "keep off",
                    "Please keep off the grass."));

            Assert.Equal("keep off", tokenizer.LastSourceText);
            Assert.Equal(64, tokenizer.LastMaximumTokens);
            Assert.Equal(new[] { 11, 12, 0 }, model.LastSourceTokenIds);
            Assert.Same(options, model.LastOptions);
            Assert.Equal("立ち入らない", result.TranslatedText);
            Assert.Equal(3, result.SourceTokenCount);
            Assert.Equal(3, result.GeneratedTokenCount);
            Assert.True(result.SourceWasTruncated);
            Assert.Equal(TranslationGenerationStopReason.EndOfSequence, result.StopReason);
        }

        [Fact]
        public async Task TranslationEngineKeepsExistingLanguagePipelineBoundary()
        {
            var runtime = new RecordingRuntime(
                new OfflineTranslationResult(
                    "出口",
                    2,
                    2,
                    false,
                    TranslationGenerationStopReason.EndOfSequence));
            var engine = new OfflineTranslationEngine(runtime);

            var text = await engine.TranslateAsync("exit", "Emergency exit");

            Assert.Equal("出口", text);
            Assert.Equal("exit", runtime.LastRequest.SourceText);
            Assert.Equal("Emergency exit", runtime.LastRequest.Context);
        }

        [Fact]
        public async Task EmptySourceReturnsEmptyWithoutInvokingRuntime()
        {
            var runtime = new RecordingRuntime(
                new OfflineTranslationResult(
                    "unused",
                    1,
                    1,
                    false,
                    TranslationGenerationStopReason.EndOfSequence));
            var engine = new OfflineTranslationEngine(runtime);

            var translated = await engine.TranslateAsync(string.Empty, "context");

            Assert.Equal(string.Empty, translated);
            Assert.Equal(0, runtime.CallCount);
        }

        [Fact]
        public async Task EmptyDecodedTranslationFailsLoudly()
        {
            var tokenizer = new RecordingTokenizer("   ");
            var model = new RecordingModel(
                new TranslationGenerationResult(
                    new[] { 0 },
                    TranslationGenerationStopReason.EndOfSequence));
            var runtime = new OfflineSeq2SeqTranslationRuntime(tokenizer, model);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.TranslateAsync(new OfflineTranslationRequest("exit", "exit")));
        }

        [Fact]
        public async Task ExternalCancellationStopsBeforeTokenization()
        {
            var tokenizer = new RecordingTokenizer("出口");
            var model = new RecordingModel(
                new TranslationGenerationResult(
                    new[] { 0 },
                    TranslationGenerationStopReason.EndOfSequence));
            var runtime = new OfflineSeq2SeqTranslationRuntime(tokenizer, model);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runtime.TranslateAsync(
                    new OfflineTranslationRequest("exit", "exit"),
                    cancellation.Token));
            Assert.Null(tokenizer.LastSourceText);
            Assert.Null(model.LastSourceTokenIds);
        }

        [Fact]
        public void MarianTokenizerMapsSentencePiecesThroughExternalVocabularyAndAppendsEos()
        {
            var source = new FakeSentencePieceProcessor(new[] { "▁keep", "▁off", "not-in-vocab" });
            var target = new FakeSentencePieceProcessor(Array.Empty<string>());
            var tokenizer = new MarianSentencePieceTokenizer(source, target, BuildMarianFixtureVocabulary());

            var encoded = tokenizer.EncodeSource("keep off mystery", 8);

            Assert.Equal(new[] { 2, 3, 1, 0 }, encoded.TokenIds);
            Assert.False(encoded.WasTruncated);
            Assert.Equal(1, tokenizer.UnknownTokenId);
            Assert.Equal(0, tokenizer.EosTokenId);
            Assert.Equal(46275, tokenizer.PadTokenId);
        }

        [Fact]
        public void MarianTokenizerTruncatesPiecesBeforeReservedEosSlot()
        {
            var source = new FakeSentencePieceProcessor(new[] { "▁keep", "▁off", "not-in-vocab" });
            var target = new FakeSentencePieceProcessor(Array.Empty<string>());
            var tokenizer = new MarianSentencePieceTokenizer(source, target, BuildMarianFixtureVocabulary());

            var encoded = tokenizer.EncodeSource("keep off mystery", 3);

            Assert.Equal(new[] { 2, 3, 0 }, encoded.TokenIds);
            Assert.True(encoded.WasTruncated);
        }

        [Fact]
        public void MarianTokenizerDecodesExternalVocabularyAndSkipsGenerationSpecials()
        {
            var source = new FakeSentencePieceProcessor(Array.Empty<string>());
            var target = new FakeSentencePieceProcessor(
                Array.Empty<string>(),
                new Dictionary<int, string> { { 99, "fallback" } });
            var tokenizer = new MarianSentencePieceTokenizer(source, target, BuildMarianFixtureVocabulary());

            var decoded = tokenizer.DecodeTarget(new[] { 10, 11, 0, 46275, 99 });

            Assert.Equal("立入らないfallback", decoded);
            Assert.Equal(new[] { "▁立入", "らない", "fallback" }, target.LastDecodedPieces);
        }

        [Fact]
        public void MarianTokenizerRejectsSpecialTokenIdDrift()
        {
            var vocabulary = BuildMarianFixtureVocabulary();
            vocabulary["wrong"] = 7;

            Assert.Throws<ArgumentException>(() =>
                new MarianSentencePieceTokenizer(
                    new FakeSentencePieceProcessor(new[] { "▁keep" }),
                    new FakeSentencePieceProcessor(Array.Empty<string>()),
                    vocabulary,
                    eosTokenId: 7,
                    padTokenId: 46275));
        }

        [Fact]
        public void ReviewedOpusMarianMetadataPassesStrictContract()
        {
            var metadata = ReviewedMetadata();

            var report = OpusMtEnJaMarianContract.Validate(metadata);

            Assert.Same(metadata, report.Metadata);
            Assert.Contains("en->jap", report.ToString());
            Assert.Contains("vocab=46276", report.ToString());
        }

        [Fact]
        public void MarianVocabularyDriftFailsBeforeRuntimeUse()
        {
            var metadata = new MarianTranslationMetadata(
                "marian",
                "MarianMTModel",
                "en",
                "jap",
                46277,
                46276,
                512,
                6,
                6,
                512,
                0,
                0,
                46275,
                46275,
                4);

            var error = Assert.Throws<InvalidOperationException>(() =>
                OpusMtEnJaMarianContract.Validate(metadata));
            Assert.Contains("vocab_size", error.Message);
        }

        [Fact]
        public void MarianGenerationCannotExceedReviewedPositionLimit()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                OpusMtEnJaMarianContract.ValidateGenerationOptions(
                    new TranslationGenerationOptions(513, 128, 1)));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                OpusMtEnJaMarianContract.ValidateGenerationOptions(
                    new TranslationGenerationOptions(128, 513, 1)));

            OpusMtEnJaMarianContract.ValidateGenerationOptions(
                new TranslationGenerationOptions(128, 128, 1));
        }

        private static Dictionary<string, int> BuildMarianFixtureVocabulary()
        {
            return new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "</s>", 0 },
                { "<unk>", 1 },
                { "▁keep", 2 },
                { "▁off", 3 },
                { "▁立入", 10 },
                { "らない", 11 },
                { "<eop>", 20 },
                { "<eod>", 21 },
                { "<pad>", 46275 }
            };
        }

        private static MarianTranslationMetadata ReviewedMetadata()
        {
            return new MarianTranslationMetadata(
                "marian",
                "MarianMTModel",
                "en",
                "jap",
                46276,
                46276,
                512,
                6,
                6,
                512,
                0,
                0,
                46275,
                46275,
                4);
        }

        private sealed class FakeSentencePieceProcessor : ISentencePieceProcessor
        {
            private readonly IReadOnlyList<string> encodedPieces;
            private readonly IReadOnlyDictionary<int, string> fallbackPieces;

            public FakeSentencePieceProcessor(
                IReadOnlyList<string> encodedPieces,
                IReadOnlyDictionary<int, string> fallbackPieces = null)
            {
                this.encodedPieces = encodedPieces;
                this.fallbackPieces = fallbackPieces ?? new Dictionary<int, string>();
            }

            public IReadOnlyList<string> LastDecodedPieces { get; private set; }

            public IReadOnlyList<string> EncodePieces(string text)
            {
                return encodedPieces;
            }

            public string DecodePieces(IReadOnlyList<string> pieces)
            {
                LastDecodedPieces = pieces.ToArray();
                return string.Concat(pieces).Replace("▁", " ").Trim();
            }

            public bool TryGetPiece(int sentencePieceId, out string piece)
            {
                return fallbackPieces.TryGetValue(sentencePieceId, out piece);
            }
        }

        private sealed class RecordingTokenizer : ITranslationTokenizer
        {
            private readonly string decoded;

            public RecordingTokenizer(string decoded)
            {
                this.decoded = decoded;
            }

            public string LastSourceText { get; private set; }
            public int LastMaximumTokens { get; private set; }

            public TranslationTokenSequence EncodeSource(string sourceText, int maximumTokens)
            {
                LastSourceText = sourceText;
                LastMaximumTokens = maximumTokens;
                return new TranslationTokenSequence(new[] { 11, 12, 0 }, true);
            }

            public string DecodeTarget(IReadOnlyList<int> targetTokenIds)
            {
                return decoded;
            }
        }

        private sealed class RecordingModel : ISeq2SeqTranslationModel
        {
            private readonly TranslationGenerationResult result;

            public RecordingModel(TranslationGenerationResult result)
            {
                this.result = result;
            }

            public IReadOnlyList<int> LastSourceTokenIds { get; private set; }
            public TranslationGenerationOptions LastOptions { get; private set; }

            public Task<TranslationGenerationResult> GenerateAsync(
                IReadOnlyList<int> sourceTokenIds,
                TranslationGenerationOptions options,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastSourceTokenIds = sourceTokenIds;
                LastOptions = options;
                return Task.FromResult(result);
            }
        }

        private sealed class RecordingRuntime : IOfflineTranslationRuntime
        {
            private readonly OfflineTranslationResult result;

            public RecordingRuntime(OfflineTranslationResult result)
            {
                this.result = result;
            }

            public int CallCount { get; private set; }
            public OfflineTranslationRequest LastRequest { get; private set; }

            public Task<OfflineTranslationResult> TranslateAsync(
                OfflineTranslationRequest request,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                LastRequest = request;
                return Task.FromResult(result);
            }
        }
    }
}
