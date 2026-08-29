using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhraseLayer.Tokenization.Microsoft;

internal static class Program
{
    private const int MaximumTokens = 512;

    public static int Main(string[] args)
    {
        try
        {
            var options = ParseArgs(args);
            var snapshotDir = new DirectoryInfo(Require(options, "snapshot-dir"));
            var referencePath = new FileInfo(Require(options, "reference"));
            var expectedRevision = Require(options, "revision");

            if (!snapshotDir.Exists) throw new InvalidOperationException("Snapshot directory does not exist: " + snapshotDir.FullName);
            if (!referencePath.Exists) throw new InvalidOperationException("Reference file does not exist: " + referencePath.FullName);

            var reference = JsonSerializer.Deserialize<ReferenceDocument>(File.ReadAllText(referencePath.FullName))
                ?? throw new InvalidOperationException("Reference JSON was empty.");
            if (reference.SchemaVersion != 1) throw new InvalidOperationException("Unsupported reference schema: " + reference.SchemaVersion);
            if (!string.Equals(reference.Revision, expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException($"Reference revision {reference.Revision} did not match {expectedRevision}.");
            if (!string.Equals(reference.ModelId, "Helsinki-NLP/opus-mt-en-jap", StringComparison.Ordinal))
                throw new InvalidOperationException("Reference model_id drift: " + reference.ModelId);
            if (reference.Cases == null || reference.Cases.Count == 0)
                throw new InvalidOperationException("Reference did not contain parity cases.");
            if (reference.CaseCount != reference.Cases.Count)
                throw new InvalidOperationException("Reference case_count did not match cases length.");

            var sourceSpm = File.ReadAllBytes(Path.Combine(snapshotDir.FullName, "source.spm"));
            var targetSpm = File.ReadAllBytes(Path.Combine(snapshotDir.FullName, "target.spm"));
            var vocabularyJson = File.ReadAllText(Path.Combine(snapshotDir.FullName, "vocab.json"));
            var sourceProcessor = new MicrosoftMlSentencePieceProcessor(sourceSpm);
            var tokenizer = MicrosoftMlMarianTokenizerFactory.Create(sourceSpm, targetSpm, vocabularyJson);

            var failures = new List<string>();
            foreach (var item in reference.Cases)
            {
                if (item == null || string.IsNullOrEmpty(item.Id) || item.Text == null)
                {
                    failures.Add("invalid case entry in reference");
                    continue;
                }

                var actualPieces = sourceProcessor.EncodePieces(item.Text).ToArray();
                var actualEncoded = tokenizer.EncodeSource(item.Text, MaximumTokens);
                var actualIds = actualEncoded.TokenIds.ToArray();

                if (actualEncoded.WasTruncated)
                    failures.Add($"{item.Id}: managed tokenizer unexpectedly truncated the parity case");
                if (!actualPieces.SequenceEqual(item.Pieces ?? Array.Empty<string>(), StringComparer.Ordinal))
                    failures.Add(DescribeMismatch(item.Id, "pieces", item.Pieces ?? Array.Empty<string>(), actualPieces));
                if (!actualIds.SequenceEqual(item.InputIds ?? Array.Empty<int>()))
                    failures.Add(DescribeMismatch(item.Id, "input_ids", item.InputIds ?? Array.Empty<int>(), actualIds));
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine($"FAIL: Marian tokenizer parity mismatches={failures.Count}/{reference.Cases.Count}");
                foreach (var failure in failures) Console.Error.WriteLine(failure);
                return 1;
            }

            Console.WriteLine($"PASS: Marian tokenizer parity cases={reference.Cases.Count}; revision={reference.Revision}; " +
                              $"dummyPrefix={sourceProcessor.AddDummyPrefix}; escapeWhitespaces={sourceProcessor.EscapeWhiteSpaces}; " +
                              $"whitespaceSuffix={sourceProcessor.TreatWhitespaceAsSuffix}; byteFallback={sourceProcessor.ByteFallback}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("ERROR: " + error);
            return 2;
        }
    }

    private static string DescribeMismatch<T>(string id, string field, IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        return $"{id}: {field} mismatch\n  expected={JsonSerializer.Serialize(expected)}\n  actual={JsonSerializer.Serialize(actual)}";
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Expected --name value argument pairs.");
            output[args[index].Substring(2)] = args[index + 1];
        }
        return output;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Missing required --" + name + " argument.");
        return value;
    }

    private sealed class ReferenceDocument
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }
        [JsonPropertyName("model_id")]
        public string? ModelId { get; set; }
        [JsonPropertyName("revision")]
        public string? Revision { get; set; }
        [JsonPropertyName("case_count")]
        public int CaseCount { get; set; }
        [JsonPropertyName("cases")]
        public List<ReferenceCase?>? Cases { get; set; }
    }

    private sealed class ReferenceCase
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
        [JsonPropertyName("text")]
        public string? Text { get; set; }
        [JsonPropertyName("pieces")]
        public string[]? Pieces { get; set; }
        [JsonPropertyName("input_ids")]
        public int[]? InputIds { get; set; }
    }
}
