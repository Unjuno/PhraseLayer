using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhraseLayer.Core.Translation;

namespace PhraseLayer.TokenizerParityProbe
{
    internal static class Program
    {
        private const int DecoderStartTokenId = 46275;
        private const int EosTokenId = 0;
        private const int PadTokenId = 46275;

        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: PhraseLayer.TokenizerParityProbe <managed-manifest> <translation-probe-json>");
                return 64;
            }

            var manifestPath = args[0];
            var reportPath = args[1];
            var tokenizer = ManagedSentencePieceManifest.ParseTokenizer(File.ReadAllText(manifestPath));
            var report = JsonNode.Parse(File.ReadAllText(reportPath)) as JsonObject;
            if (report == null)
                throw new InvalidDataException("Translation probe report must be a JSON object.");

            var encodeComparisons = VerifySourceEncoding(tokenizer, RequiredArray(report, "tokenizer_reference", "samples"));
            var decodeComparisons = VerifyTargetDecoding(tokenizer, RequiredArray(report, "reference_samples"));
            var exact = AllExact(encodeComparisons) && AllExact(decodeComparisons);

            report["managed_tokenizer_parity"] = new JsonObject
            {
                ["schema_version"] = 1,
                ["exact"] = exact,
                ["runtime"] = "PhraseLayer.Core.ManagedSentencePieceUnigramTokenizer",
                ["source_encode"] = encodeComparisons,
                ["target_decode"] = decodeComparisons,
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(reportPath, report.ToJsonString(options) + "\n");

            if (!exact)
            {
                Console.Error.WriteLine("Managed SentencePiece parity FAILED. See managed_tokenizer_parity in the probe report.");
                return 2;
            }

            Console.WriteLine(
                "Managed SentencePiece parity PASS: source encode fixtures=" + encodeComparisons.Count +
                " target decode fixtures=" + decodeComparisons.Count + ".");
            return 0;
        }

        private static JsonArray VerifySourceEncoding(
            ITranslationTokenizer tokenizer,
            JsonArray samples)
        {
            var comparisons = new JsonArray();
            for (var index = 0; index < samples.Count; index++)
            {
                var sample = samples[index] as JsonObject;
                if (sample == null) throw new InvalidDataException("Tokenizer source fixture must be an object.");
                var source = RequiredString(sample, "source");
                var expected = ReadIntArray(RequiredArray(sample, "input_ids"));
                var actual = tokenizer.Encode(source);
                var equal = SequenceEqual(expected, actual);
                comparisons.Add(new JsonObject
                {
                    ["source"] = source,
                    ["exact"] = equal,
                    ["expected_ids"] = ToJsonArray(expected),
                    ["actual_ids"] = ToJsonArray(actual),
                });
            }
            return comparisons;
        }

        private static JsonArray VerifyTargetDecoding(
            ITranslationTokenizer tokenizer,
            JsonArray referenceSamples)
        {
            var comparisons = new JsonArray();
            for (var index = 0; index < referenceSamples.Count; index++)
            {
                var sample = referenceSamples[index] as JsonObject;
                if (sample == null) throw new InvalidDataException("Translation reference fixture must be an object.");
                var source = RequiredString(sample, "source");
                var expectedTranslation = RequiredString(sample, "translation");
                var generated = ReadIntArray(RequiredArray(sample, "token_ids"));
                var semanticTokens = StripDecoderControlTokens(generated);
                var actualTranslation = tokenizer.Decode(semanticTokens);
                comparisons.Add(new JsonObject
                {
                    ["source"] = source,
                    ["exact"] = string.Equals(expectedTranslation, actualTranslation, StringComparison.Ordinal),
                    ["expected_translation"] = expectedTranslation,
                    ["actual_translation"] = actualTranslation,
                    ["decoded_token_ids"] = ToJsonArray(semanticTokens),
                });
            }
            return comparisons;
        }

        private static List<int> StripDecoderControlTokens(IReadOnlyList<int> generated)
        {
            var result = new List<int>();
            for (var index = 0; index < generated.Count; index++)
            {
                var tokenId = generated[index];
                if (index == 0 && tokenId == DecoderStartTokenId)
                    continue;
                if (tokenId == EosTokenId)
                    break;
                if (tokenId == PadTokenId)
                    continue;
                result.Add(tokenId);
            }
            return result;
        }

        private static bool AllExact(JsonArray comparisons)
        {
            for (var index = 0; index < comparisons.Count; index++)
            {
                var item = comparisons[index] as JsonObject;
                if (item == null || item["exact"] == null || item["exact"].GetValue<bool>() != true)
                    return false;
            }
            return true;
        }

        private static JsonArray RequiredArray(JsonObject root, params string[] path)
        {
            JsonNode current = root;
            for (var index = 0; index < path.Length; index++)
            {
                var obj = current as JsonObject;
                if (obj == null || obj[path[index]] == null)
                    throw new InvalidDataException("Missing JSON path: " + string.Join(".", path));
                current = obj[path[index]];
            }
            var array = current as JsonArray;
            if (array == null)
                throw new InvalidDataException("JSON path is not an array: " + string.Join(".", path));
            return array;
        }

        private static string RequiredString(JsonObject obj, string key)
        {
            var node = obj[key];
            if (node == null) throw new InvalidDataException("Missing JSON string: " + key);
            return node.GetValue<string>();
        }

        private static List<int> ReadIntArray(JsonArray array)
        {
            var result = new List<int>(array.Count);
            for (var index = 0; index < array.Count; index++)
            {
                var node = array[index];
                if (node == null) throw new InvalidDataException("Integer array contains null.");
                result.Add(node.GetValue<int>());
            }
            return result;
        }

        private static bool SequenceEqual(IReadOnlyList<int> expected, IReadOnlyList<int> actual)
        {
            if (expected.Count != actual.Count) return false;
            for (var index = 0; index < expected.Count; index++)
            {
                if (expected[index] != actual[index]) return false;
            }
            return true;
        }

        private static JsonArray ToJsonArray(IReadOnlyList<int> values)
        {
            var array = new JsonArray();
            for (var index = 0; index < values.Count; index++)
                array.Add(values[index]);
            return array;
        }
    }
}
