using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PhraseLayer.Core.Translation
{
    /// <summary>
    /// Deterministic, dependency-free runtime fixture format for the pinned OPUS-MT tokenizer.
    /// The probe produces this file only after exact source-tokenizer and managed-tokenizer parity pass.
    /// Text payloads are base64 UTF-8 so tabs/newlines cannot corrupt the format.
    /// </summary>
    public static class TranslationTokenizerFixtureManifest
    {
        public const string Magic = "PHRASELAYER_TRANSLATION_TOKENIZER_FIXTURES_V1";

        public static TranslationTokenizerFixtureSet Parse(string manifestText)
        {
            if (manifestText == null) throw new ArgumentNullException(nameof(manifestText));

            var lines = SplitLines(manifestText);
            if (lines.Count == 0 || !string.Equals(lines[0], Magic, StringComparison.Ordinal))
                throw new FormatException("Translation tokenizer fixture manifest magic/version mismatch.");

            var header = new Dictionary<string, string>(StringComparer.Ordinal);
            var encode = new List<TranslationTokenizerEncodeFixture>();
            var decode = new List<TranslationTokenizerDecodeFixture>();
            var phase = 0;

            for (var lineIndex = 1; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                if (line.Length == 0) continue;

                if (string.Equals(line, "END_HEADER", StringComparison.Ordinal))
                {
                    if (phase != 0) throw new FormatException("Duplicate tokenizer fixture END_HEADER marker.");
                    phase = 1;
                    continue;
                }

                if (string.Equals(line, "END", StringComparison.Ordinal))
                {
                    if (phase != 1) throw new FormatException("Tokenizer fixture END marker appeared before END_HEADER.");
                    phase = 2;
                    continue;
                }

                if (phase == 2)
                    throw new FormatException("Tokenizer fixture manifest contains content after END.");

                var fields = line.Split('\t');
                if (phase == 0)
                {
                    if (fields.Length != 2 || fields[0].Length == 0)
                        throw new FormatException("Invalid tokenizer fixture header line: " + line);
                    if (!header.TryAdd(fields[0], fields[1]))
                        throw new FormatException("Duplicate tokenizer fixture header key: " + fields[0]);
                    continue;
                }

                if (fields.Length == 3 && string.Equals(fields[0], "E", StringComparison.Ordinal))
                {
                    encode.Add(new TranslationTokenizerEncodeFixture(
                        DecodeBase64Utf8(fields[1], "encode source"),
                        ParseTokenIds(fields[2], "encode token ids")));
                    continue;
                }

                if (fields.Length == 3 && string.Equals(fields[0], "D", StringComparison.Ordinal))
                {
                    decode.Add(new TranslationTokenizerDecodeFixture(
                        ParseTokenIds(fields[1], "decode token ids"),
                        DecodeBase64Utf8(fields[2], "decode expected text")));
                    continue;
                }

                throw new FormatException("Invalid tokenizer fixture data line: " + line);
            }

            if (phase != 2)
                throw new FormatException("Tokenizer fixture manifest is missing END marker.");

            var modelId = DecodeBase64Utf8(Required(header, "model_id_b64"), "model id");
            var revision = Required(header, "revision");
            if (!string.Equals(modelId, LocalTranslationStagingContract.ExpectedModelId, StringComparison.Ordinal))
                throw new FormatException("Tokenizer fixture model id drift: " + modelId);
            if (!string.Equals(revision, LocalTranslationStagingContract.ExpectedRevision, StringComparison.Ordinal))
                throw new FormatException("Tokenizer fixture revision drift: " + revision);

            var expectedEncode = ParseInt(Required(header, "encode_fixture_count"), "encode fixture count");
            var expectedDecode = ParseInt(Required(header, "decode_fixture_count"), "decode fixture count");
            if (encode.Count != expectedEncode)
                throw new FormatException("Encode fixture count mismatch: header=" + expectedEncode + " parsed=" + encode.Count + ".");
            if (decode.Count != expectedDecode)
                throw new FormatException("Decode fixture count mismatch: header=" + expectedDecode + " parsed=" + decode.Count + ".");

            return new TranslationTokenizerFixtureSet(encode, decode);
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
                throw new FormatException("Tokenizer fixture manifest is missing header: " + key);
            return value;
        }

        private static int ParseInt(string value, string label)
        {
            int parsed;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed < 0)
                throw new FormatException("Invalid " + label + ": " + value);
            return parsed;
        }

        private static IReadOnlyList<int> ParseTokenIds(string value, string label)
        {
            if (string.IsNullOrEmpty(value))
                throw new FormatException(label + " is empty.");

            var fields = value.Split(',');
            var result = new int[fields.Length];
            for (var index = 0; index < fields.Length; index++)
                result[index] = ParseInt(fields[index], label + "[" + index + "]");
            return result;
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
