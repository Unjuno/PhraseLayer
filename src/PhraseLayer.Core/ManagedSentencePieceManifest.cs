using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PhraseLayer.Core.Translation
{
    /// <summary>
    /// Deterministic text format used to move measured SentencePiece UNIGRAM data from the local Python staging
    /// boundary into the pure-managed Quest runtime. Piece strings are base64 UTF-8 so whitespace/control-looking
    /// content can never corrupt line parsing.
    /// </summary>
    public static class ManagedSentencePieceManifest
    {
        public const string Magic = "PHRASELAYER_SENTENCEPIECE_UNIGRAM_V1";

        public static ManagedSentencePieceUnigramTokenizer ParseTokenizer(string manifestText)
        {
            if (manifestText == null) throw new ArgumentNullException(nameof(manifestText));
            var lines = SplitLines(manifestText);
            if (lines.Count == 0 || !string.Equals(lines[0], Magic, StringComparison.Ordinal))
                throw new FormatException("Managed SentencePiece manifest magic/version mismatch.");

            var header = new Dictionary<string, string>(StringComparer.Ordinal);
            var sourcePieces = new List<SentencePieceUnigramPiece>();
            var targetVocabulary = new Dictionary<int, string>();
            var phase = 0;

            for (var lineIndex = 1; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                if (line.Length == 0) continue;
                if (string.Equals(line, "END_HEADER", StringComparison.Ordinal))
                {
                    if (phase != 0) throw new FormatException("Duplicate END_HEADER marker.");
                    phase = 1;
                    continue;
                }
                if (string.Equals(line, "END", StringComparison.Ordinal))
                {
                    if (phase != 1) throw new FormatException("END marker appeared before END_HEADER.");
                    phase = 2;
                    continue;
                }
                if (phase == 2)
                    throw new FormatException("Managed SentencePiece manifest contains content after END.");

                var fields = line.Split('\t');
                if (phase == 0)
                {
                    if (fields.Length != 2 || fields[0].Length == 0)
                        throw new FormatException("Invalid SentencePiece manifest header line: " + line);
                    if (!header.TryAdd(fields[0], fields[1]))
                        throw new FormatException("Duplicate SentencePiece manifest header key: " + fields[0]);
                    continue;
                }

                if (fields.Length == 4 && string.Equals(fields[0], "S", StringComparison.Ordinal))
                {
                    var modelTokenId = ParseInt(fields[1], "source model token id");
                    var score = ParseDouble(fields[2], "source unigram score");
                    var piece = DecodeBase64Utf8(fields[3], "source piece");
                    sourcePieces.Add(new SentencePieceUnigramPiece(piece, score, modelTokenId));
                    continue;
                }
                if (fields.Length == 3 && string.Equals(fields[0], "V", StringComparison.Ordinal))
                {
                    var modelTokenId = ParseInt(fields[1], "target vocabulary token id");
                    var piece = DecodeBase64Utf8(fields[2], "target vocabulary piece");
                    if (!targetVocabulary.TryAdd(modelTokenId, piece))
                        throw new FormatException("Duplicate target vocabulary token id: " + modelTokenId);
                    continue;
                }

                throw new FormatException("Invalid SentencePiece manifest data line: " + line);
            }

            if (phase != 2)
                throw new FormatException("Managed SentencePiece manifest is missing END marker.");

            var measured = new SentencePieceRuntimeManifest(
                Required(header, "model_type"),
                Required(header, "normalizer_name"),
                Required(header, "normalizer_charsmap_sha256"),
                ParseInt(Required(header, "source_total_piece_count"), "source total piece count"),
                ParseInt(Required(header, "target_total_piece_count"), "target total piece count"),
                ParseBool(Required(header, "byte_fallback"), "byte_fallback"),
                ParseBool(Required(header, "add_dummy_prefix"), "add_dummy_prefix"),
                ParseBool(Required(header, "remove_extra_whitespaces"), "remove_extra_whitespaces"),
                ParseBool(Required(header, "escape_whitespaces"), "escape_whitespaces"));
            OpusMtEnJapSentencePieceContract.ValidateMeasuredManifest(measured);

            var expectedNormalPieces = ParseInt(Required(header, "source_normal_piece_count"), "source normal piece count");
            if (sourcePieces.Count != expectedNormalPieces)
                throw new FormatException(
                    "Source NORMAL piece count mismatch: header=" + expectedNormalPieces + " parsed=" + sourcePieces.Count + ".");

            var expectedVocabularyCount = ParseInt(Required(header, "marian_vocab_count"), "Marian vocabulary count");
            if (targetVocabulary.Count != expectedVocabularyCount)
                throw new FormatException(
                    "Marian vocabulary count mismatch: header=" + expectedVocabularyCount + " parsed=" + targetVocabulary.Count + ".");
            if (expectedVocabularyCount != OpusMtEnJapMeasuredOnnxContract.VocabularySize)
                throw new FormatException("Managed tokenizer vocabulary size differs from measured ONNX logits vocabulary.");

            var modelId = DecodeBase64Utf8(Required(header, "model_id_b64"), "model id");
            var revision = Required(header, "revision");
            if (!string.Equals(modelId, LocalTranslationStagingContract.ExpectedModelId, StringComparison.Ordinal))
                throw new FormatException("Managed tokenizer model id drift: " + modelId);
            if (!string.Equals(revision, LocalTranslationStagingContract.ExpectedRevision, StringComparison.Ordinal))
                throw new FormatException("Managed tokenizer revision drift: " + revision);

            var unknownTokenId = ParseInt(Required(header, "marian_unknown_token_id"), "Marian unknown token id");
            var sourceEosTokenId = ParseInt(Required(header, "marian_eos_token_id"), "Marian EOS token id");
            if (unknownTokenId != 1)
                throw new FormatException("Pinned Marian unknown token id must remain 1.");
            if (sourceEosTokenId != OpusMtEnJapGenerationContract.EosTokenId)
                throw new FormatException("Pinned Marian source EOS token id drift.");

            return new ManagedSentencePieceUnigramTokenizer(
                sourcePieces,
                targetVocabulary,
                unknownTokenId,
                sourceEosTokenId);
        }

        private static List<string> SplitLines(string text)
        {
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            return new List<string>(normalized.Split('\n'));
        }

        private static string Required(IReadOnlyDictionary<string, string> header, string key)
        {
            string value;
            if (!header.TryGetValue(key, out value) || string.IsNullOrEmpty(value))
                throw new FormatException("Managed SentencePiece manifest is missing header: " + key);
            return value;
        }

        private static int ParseInt(string value, string label)
        {
            int parsed;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                throw new FormatException("Invalid " + label + ": " + value);
            return parsed;
        }

        private static double ParseDouble(string value, string label)
        {
            double parsed;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ||
                double.IsNaN(parsed) || double.IsInfinity(parsed))
            {
                throw new FormatException("Invalid " + label + ": " + value);
            }
            return parsed;
        }

        private static bool ParseBool(string value, string label)
        {
            if (string.Equals(value, "true", StringComparison.Ordinal)) return true;
            if (string.Equals(value, "false", StringComparison.Ordinal)) return false;
            throw new FormatException("Invalid " + label + ": " + value);
        }

        private static string DecodeBase64Utf8(string encoded, string label)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException error)
            {
                throw new FormatException("Invalid base64 for " + label + ".", error);
            }
        }
    }
}
