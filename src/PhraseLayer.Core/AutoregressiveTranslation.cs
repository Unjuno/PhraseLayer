using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PhraseLayer.Core.Translation
{
    /// <summary>
    /// Tokenizer boundary for local sequence-to-sequence translation.
    /// The initial OPUS-MT candidate uses SentencePiece, but Core deliberately does not depend on a
    /// specific tokenizer implementation or native library.
    /// </summary>
    public interface ITranslationTokenizer
    {
        IReadOnlyList<int> Encode(string text);
        string Decode(IReadOnlyList<int> tokenIds);
    }

    /// <summary>
    /// One normalized next-token candidate returned by a local decoder backend.
    /// LogProbability is required rather than an arbitrary logit so beam scores are comparable across steps.
    /// </summary>
    public readonly struct TranslationTokenCandidate
    {
        public TranslationTokenCandidate(int tokenId, double logProbability)
        {
            if (tokenId < 0) throw new ArgumentOutOfRangeException(nameof(tokenId));
            if (double.IsNaN(logProbability) || double.IsInfinity(logProbability) || logProbability > 0.0)
                throw new ArgumentOutOfRangeException(nameof(logProbability), "Log probability must be finite and <= 0.");

            TokenId = tokenId;
            LogProbability = logProbability;
        }

        public int TokenId { get; }
        public double LogProbability { get; }
    }

    /// <summary>
    /// Backend boundary for a local encoder/decoder model. A correctness-first implementation may rerun the
    /// decoder from the complete generated prefix. A later Quest optimization may cache encoder output and
    /// decoder key/value state without changing Core's search policy.
    /// </summary>
    public interface IAutoregressiveTranslationBackend
    {
        Task<IReadOnlyList<TranslationTokenCandidate>> PredictNextAsync(
            IReadOnlyList<int> sourceTokenIds,
            IReadOnlyList<int> generatedTokenIds,
            int maxCandidates,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    public sealed class TranslationGenerationOptions
    {
        public TranslationGenerationOptions(
            int decoderStartTokenId,
            int eosTokenId,
            int padTokenId,
            int maxLength,
            int beamWidth,
            double lengthPenalty = 1.0)
        {
            if (decoderStartTokenId < 0) throw new ArgumentOutOfRangeException(nameof(decoderStartTokenId));
            if (eosTokenId < 0) throw new ArgumentOutOfRangeException(nameof(eosTokenId));
            if (padTokenId < 0) throw new ArgumentOutOfRangeException(nameof(padTokenId));
            if (maxLength < 2) throw new ArgumentOutOfRangeException(nameof(maxLength));
            if (beamWidth <= 0 || beamWidth > 32) throw new ArgumentOutOfRangeException(nameof(beamWidth));
            if (double.IsNaN(lengthPenalty) || double.IsInfinity(lengthPenalty) || lengthPenalty <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(lengthPenalty));

            DecoderStartTokenId = decoderStartTokenId;
            EosTokenId = eosTokenId;
            PadTokenId = padTokenId;
            MaxLength = maxLength;
            BeamWidth = beamWidth;
            LengthPenalty = lengthPenalty;
        }

        public int DecoderStartTokenId { get; }
        public int EosTokenId { get; }
        public int PadTokenId { get; }

        /// <summary>
        /// Maximum decoder sequence length including the decoder-start token, matching the conventional
        /// seq2seq max_length definition used by the pinned Marian generation config.
        /// </summary>
        public int MaxLength { get; }

        public int BeamWidth { get; }
        public double LengthPenalty { get; }
    }

    /// <summary>
    /// Platform-neutral reference generation loop for local Marian/seq2seq translation.
    ///
    /// This class owns search policy only. It does not know ONNX, Unity, SentencePiece file formats, or model
    /// assets. The currently selected semantic source span is translated directly; the context argument remains
    /// part of ITranslationEngine for cache identity and future context-aware engines, but this reference Marian
    /// path does not manufacture a contextual prompt that the upstream model was not trained to understand.
    /// </summary>
    public sealed class AutoregressiveTranslationEngine : ITranslationEngine
    {
        private readonly ITranslationTokenizer tokenizer;
        private readonly IAutoregressiveTranslationBackend backend;
        private readonly TranslationGenerationOptions options;

        public AutoregressiveTranslationEngine(
            ITranslationTokenizer tokenizer,
            IAutoregressiveTranslationBackend backend,
            TranslationGenerationOptions options)
        {
            this.tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<string> TranslateAsync(
            string sourceText,
            string context,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (sourceText == null) throw new ArgumentNullException(nameof(sourceText));
            if (context == null) throw new ArgumentNullException(nameof(context));
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(sourceText))
                return sourceText;

            var sourceTokenIds = tokenizer.Encode(sourceText);
            if (sourceTokenIds == null || sourceTokenIds.Count == 0)
                throw new InvalidOperationException("Translation tokenizer produced no source tokens.");

            var beams = new List<BeamState>(options.BeamWidth)
            {
                BeamState.Start(options.DecoderStartTokenId)
            };

            // MaxLength includes the decoder-start token, so each iteration appends at most one token.
            for (var decoderLength = 1; decoderLength < options.MaxLength; decoderLength++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expanded = new List<BeamState>(checked(options.BeamWidth * options.BeamWidth));
                var allPreviouslyComplete = true;

                for (var beamIndex = 0; beamIndex < beams.Count; beamIndex++)
                {
                    var beam = beams[beamIndex];
                    if (beam.Completed)
                    {
                        expanded.Add(beam);
                        continue;
                    }

                    allPreviouslyComplete = false;
                    var candidates = await backend.PredictNextAsync(
                        sourceTokenIds,
                        beam.TokenIds,
                        options.BeamWidth,
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (candidates == null || candidates.Count == 0)
                        throw new InvalidOperationException("Translation backend returned no next-token candidates.");

                    var ranked = RankAndValidateCandidates(candidates);
                    var accepted = 0;
                    for (var candidateIndex = 0;
                         candidateIndex < ranked.Count && accepted < options.BeamWidth;
                         candidateIndex++)
                    {
                        var candidate = ranked[candidateIndex];

                        // Padding is structural, not semantic output. If a backend ranks PAD first, skip it and
                        // continue to the next real candidate. EOS is still accepted even when EOS == PAD on a
                        // different model contract because equality is checked first.
                        if (candidate.TokenId == options.PadTokenId &&
                            candidate.TokenId != options.EosTokenId)
                        {
                            continue;
                        }

                        expanded.Add(beam.Append(
                            candidate.TokenId,
                            candidate.LogProbability,
                            candidate.TokenId == options.EosTokenId));
                        accepted++;
                    }

                    if (accepted == 0)
                        throw new InvalidOperationException("Translation backend produced only padding candidates.");
                }

                if (allPreviouslyComplete)
                    break;

                expanded.Sort((left, right) => CompareBeams(left, right, options.LengthPenalty));
                if (expanded.Count > options.BeamWidth)
                    expanded.RemoveRange(options.BeamWidth, expanded.Count - options.BeamWidth);
                beams = expanded;

                var allComplete = true;
                for (var index = 0; index < beams.Count; index++)
                {
                    if (!beams[index].Completed)
                    {
                        allComplete = false;
                        break;
                    }
                }
                if (allComplete)
                    break;
            }

            if (beams.Count == 0)
                throw new InvalidOperationException("Translation search produced no beams.");

            beams.Sort((left, right) => CompareBeams(left, right, options.LengthPenalty));
            var best = SelectBestCompletedOrBestPartial(beams, options.LengthPenalty);
            var outputTokenIds = RemoveControlTokens(best.TokenIds);
            if (outputTokenIds.Count == 0)
                throw new InvalidOperationException("Translation search produced no decodable output tokens.");

            var decoded = tokenizer.Decode(outputTokenIds);
            if (string.IsNullOrWhiteSpace(decoded))
                throw new InvalidOperationException("Translation tokenizer decoded an empty result.");
            return decoded;
        }

        private List<TranslationTokenCandidate> RankAndValidateCandidates(
            IReadOnlyList<TranslationTokenCandidate> candidates)
        {
            var ranked = new List<TranslationTokenCandidate>(candidates.Count);
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                // Reconstructing validates values even if an implementation materialized an invalid struct through
                // default(T) or a serializer instead of the public constructor.
                ranked.Add(new TranslationTokenCandidate(candidate.TokenId, candidate.LogProbability));
            }

            ranked.Sort((left, right) =>
            {
                var probabilityOrder = right.LogProbability.CompareTo(left.LogProbability);
                return probabilityOrder != 0
                    ? probabilityOrder
                    : left.TokenId.CompareTo(right.TokenId);
            });
            return ranked;
        }

        private List<int> RemoveControlTokens(IReadOnlyList<int> tokens)
        {
            var output = new List<int>(tokens.Count);
            for (var index = 0; index < tokens.Count; index++)
            {
                var tokenId = tokens[index];
                if (index == 0 && tokenId == options.DecoderStartTokenId)
                    continue;
                if (tokenId == options.EosTokenId)
                    break;
                if (tokenId == options.PadTokenId)
                    continue;
                output.Add(tokenId);
            }
            return output;
        }

        private static BeamState SelectBestCompletedOrBestPartial(
            IReadOnlyList<BeamState> rankedBeams,
            double lengthPenalty)
        {
            BeamState? bestCompleted = null;
            for (var index = 0; index < rankedBeams.Count; index++)
            {
                var candidate = rankedBeams[index];
                if (!candidate.Completed) continue;
                if (!bestCompleted.HasValue ||
                    CompareBeams(candidate, bestCompleted.Value, lengthPenalty) < 0)
                {
                    bestCompleted = candidate;
                }
            }

            return bestCompleted ?? rankedBeams[0];
        }

        private static int CompareBeams(BeamState left, BeamState right, double lengthPenalty)
        {
            var scoreOrder = right.NormalizedScore(lengthPenalty).CompareTo(left.NormalizedScore(lengthPenalty));
            if (scoreOrder != 0) return scoreOrder;

            var rawOrder = right.LogProbability.CompareTo(left.LogProbability);
            if (rawOrder != 0) return rawOrder;

            var shared = Math.Min(left.TokenIds.Count, right.TokenIds.Count);
            for (var index = 0; index < shared; index++)
            {
                var tokenOrder = left.TokenIds[index].CompareTo(right.TokenIds[index]);
                if (tokenOrder != 0) return tokenOrder;
            }
            return left.TokenIds.Count.CompareTo(right.TokenIds.Count);
        }

        private readonly struct BeamState
        {
            private BeamState(int[] tokenIds, double logProbability, bool completed)
            {
                TokenIds = tokenIds;
                LogProbability = logProbability;
                Completed = completed;
            }

            public IReadOnlyList<int> TokenIds { get; }
            public double LogProbability { get; }
            public bool Completed { get; }

            public static BeamState Start(int decoderStartTokenId)
            {
                return new BeamState(new[] { decoderStartTokenId }, 0.0, false);
            }

            public BeamState Append(int tokenId, double logProbability, bool completed)
            {
                var appended = new int[TokenIds.Count + 1];
                for (var index = 0; index < TokenIds.Count; index++)
                    appended[index] = TokenIds[index];
                appended[appended.Length - 1] = tokenId;
                return new BeamState(appended, LogProbability + logProbability, completed);
            }

            public double NormalizedScore(double lengthPenalty)
            {
                var generatedLength = Math.Max(1, TokenIds.Count - 1);
                return LogProbability / Math.Pow(generatedLength, lengthPenalty);
            }
        }
    }
}
