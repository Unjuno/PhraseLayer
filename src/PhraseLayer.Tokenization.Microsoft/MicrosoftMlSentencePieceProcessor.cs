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

            return tokens.Select(token => token.Value).ToArray();
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
