using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.Tokenizers;
using PhraseLayer.Core.Translation;

namespace PhraseLayer.Tokenization.Microsoft
{
    /// <summary>
    /// Pure-managed SentencePiece adapter backed by Microsoft.ML.Tokenizers 2.0.0.
    /// The exact embedded normalizer and Unigram/BPE model are parsed from the supplied .spm bytes.
    /// BOS/EOS insertion is disabled here because MarianSentencePieceTokenizer owns the Marian model-level EOS policy.
    /// </summary>
    public sealed class MicrosoftMlSentencePieceProcessor : ISentencePieceProcessor
    {
        private readonly SentencePieceTokenizer tokenizer;
        private readonly IReadOnlyDictionary<int, string> piecesById;

        public MicrosoftMlSentencePieceProcessor(byte[] modelBytes)
        {
            if (modelBytes == null) throw new ArgumentNullException(nameof(modelBytes));
            if (modelBytes.Length == 0) throw new ArgumentException("SentencePiece model bytes must not be empty.", nameof(modelBytes));

            using var stream = new MemoryStream(modelBytes, writable: false);
            tokenizer = SentencePieceTokenizer.Create(
                stream,
                addBeginningOfSentence: false,
                addEndOfSentence: false);

            piecesById = tokenizer.Vocabulary.ToDictionary(
                entry => entry.Value,
                entry => entry.Key);
        }

        public int VocabularyCount => tokenizer.Vocabulary.Count;
        public bool AddDummyPrefix => tokenizer.AddDummyPrefix;
        public bool EscapeWhiteSpaces => tokenizer.EscapeWhiteSpaces;
        public bool TreatWhitespaceAsSuffix => tokenizer.TreatWhitespaceAsSuffix;
        public bool ByteFallback => tokenizer.ByteFallback;

        public IReadOnlyList<string> EncodePieces(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (text.Length == 0) return Array.Empty<string>();

            // SentencePiece owns both normalization and segmentation. Microsoft.ML.Tokenizers' optional
            // generic pre-tokenizer splits punctuation/symbol and CJK boundaries before the .spm model sees
            // them (for example "0%", "$9", or "東京"), which diverges from MarianTokenizer. Keep the
            // embedded SentencePiece normalizer enabled but bypass that extra pre-tokenization stage.
            var tokens = tokenizer.EncodeToTokens(
                text,
                out _,
                addBeginningOfSentence: false,
                addEndOfSentence: false,
                considerPreTokenization: false,
                considerNormalization: true);

            // Google's SentencePiece processor emits one surface token for a contiguous unknown span even
            // though every such span carries the single UNK id. Microsoft.ML.Tokenizers' Unigram path emits
            // one UNK token per unmatched Unicode code point instead. Marian then maps each returned surface
            // piece through its external vocab.json, so leaving those tokens split changes the model input
            // length (for example Google emits one UNK for "0%", "$9", "99", or "東京").
            //
            // An unknown surface token is observable without assuming the model's numeric UNK id: its Value
            // is the normalized source surface and is not a literal entry in the loaded SentencePiece
            // vocabulary. Coalesce only adjacent unknown surfaces carrying the same internal id. Known model
            // pieces and non-adjacent unknown regions remain unchanged.
            var pieces = new List<string>(tokens.Count);
            var previousWasUnknownSurface = false;
            var previousTokenId = -1;
            foreach (var token in tokens)
            {
                var isUnknownSurface = !tokenizer.Vocabulary.ContainsKey(token.Value);
                if (
                    isUnknownSurface
                    && previousWasUnknownSurface
                    && previousTokenId == token.Id)
                {
                    pieces[pieces.Count - 1] += token.Value;
                }
                else
                {
                    pieces.Add(token.Value);
                }

                previousWasUnknownSurface = isUnknownSurface;
                previousTokenId = token.Id;
            }

            return pieces;
        }

        public string DecodePieces(IReadOnlyList<string> pieces)
        {
            if (pieces == null) throw new ArgumentNullException(nameof(pieces));
            if (pieces.Count == 0) return string.Empty;

            var ids = new int[pieces.Count];
            for (var index = 0; index < pieces.Count; index++)
            {
                var piece = pieces[index];
                if (piece == null)
                    throw new ArgumentException("SentencePiece decode input contains a null piece.", nameof(pieces));
                if (!tokenizer.Vocabulary.TryGetValue(piece, out ids[index]))
                {
                    throw new InvalidOperationException(
                        "SentencePiece decode piece is not present in the loaded model vocabulary: " + piece);
                }
            }

            return tokenizer.Decode(ids, considerSpecialTokens: false);
        }

        public bool TryGetPiece(int sentencePieceId, out string? piece)
        {
            return piecesById.TryGetValue(sentencePieceId, out piece);
        }
    }
}
