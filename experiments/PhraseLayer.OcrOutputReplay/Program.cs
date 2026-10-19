using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhraseLayer.Core.Inputs;

if (args.Length != 4) throw new ArgumentException("Expected replay JSON, report JSON, dictionary and manifest paths.");
var checks = new List<object>(); var failed = 0;
var bytes = File.ReadAllBytes(args[0]);
using var document = JsonDocument.Parse(bytes);
var root = document.RootElement;
var expectedDictionary = root.GetProperty("dictionary").EnumerateArray().Select(value => value.GetString() ?? throw new InvalidDataException()).ToArray();
var dictionaryBytes = File.ReadAllBytes(args[2]);
var dictionarySha = Convert.ToHexString(SHA256.HashData(dictionaryBytes)).ToLowerInvariant();
var dictionaryText = new UTF8Encoding(false, true).GetString(dictionaryBytes);
var rawDictionary = PaddleOcrCharacterDictionary.Parse(dictionaryText, false);
var dictionary = PaddleOcrCharacterDictionary.Parse(dictionaryText, true);
using var manifestDocument = JsonDocument.Parse(File.ReadAllBytes(args[3]));
var m = manifestDocument.RootElement;
string Text(string key) => m.GetProperty(key).GetString() ?? throw new InvalidDataException("Missing manifest string: " + key);
var manifest = new PaddleOcrDictionaryManifest(
    m.GetProperty("schema_version").GetInt32(), Text("model_id"), Text("upstream"), Text("revision"),
    Text("source_artifact"), Text("postprocess_name"), m.GetProperty("raw_token_count").GetInt32(),
    m.GetProperty("raw_contains_literal_space").GetBoolean(), m.GetProperty("use_space_char").GetBoolean(),
    m.GetProperty("effective_token_count").GetInt32(), Text("generated_artifact"), Text("generated_sha256"));
PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(manifest, rawDictionary.Count, true, dictionarySha);
if (!dictionary.SequenceEqual(expectedDictionary)) throw new InvalidDataException("Python and production Core dictionary order differs.");
var realCases = root.GetProperty("real_model_cases").GetInt32();
if (realCases <= 0) throw new InvalidDataException("No actual model inference cases were supplied.");
var observedRealCases = 0;
foreach (var item in root.GetProperty("cases").EnumerateArray())
{
    var name = item.GetProperty("name").GetString() ?? throw new InvalidDataException("Missing replay name.");
    if (name.StartsWith("real:", StringComparison.Ordinal)) observedRealCases++;
    bool passed; string? error = null;
    try
    {
        var shape = Integers(item.GetProperty("probability_shape"));
        var packedShape = Integers(item.GetProperty("packed_shape"));
        var packedValues = item.GetProperty("packed_values").EnumerateArray().Select(value => value.GetSingle()).ToArray();
        var unpacked = PaddleCtcPackedOutput.Unpack(shape, packedShape, packedValues);
        PaddleOcrRuntimeContract.ValidateRecognizerReduced(shape, unpacked.ClassIndices, unpacked.MaxScores, dictionary.Count);
        var decoded = PaddleCtcGreedyDecoder.DecodeFromIndices(unpacked.ClassIndices, unpacked.MaxScores, dictionary);
        passed = unpacked.ClassIndices.SequenceEqual(Integers(item.GetProperty("expected_indices"))) &&
            decoded.Text == item.GetProperty("expected_text").GetString() &&
            decoded.EmittedTokenCount == item.GetProperty("expected_token_count").GetInt32() &&
            Math.Abs(decoded.Confidence - item.GetProperty("expected_confidence").GetDouble()) <= 1e-6;
    }
    catch (Exception exception) { passed = false; error = exception.GetType().Name; }
    if (!passed) failed++;
    checks.Add(new { name, passed, error_type = error });
}
if (observedRealCases != realCases) throw new InvalidDataException("Replay real-case labels differ from the preceding model observation count.");
var report = new {
    experiment = "actual-onnx-packed-output-through-production-core-decoder", safety_result = failed == 0 ? "PASS" : "FAIL",
    real_model_cases = realCases, total_replay_cases = checks.Count, failed_checks = failed, checks,
    replay_sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
    dictionary_sha256 = dictionarySha, raw_dictionary_tokens = rawDictionary.Count,
    effective_dictionary_tokens = dictionary.Count, production_dictionary_parser_executed = true,
    production_dictionary_manifest_validated = true, python_core_dictionary_order_exact = true,
    csharp_core_replay_performed = true, actual_model_executed_in_preceding_python_step = true,
    framework = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    git_commit = Environment.GetEnvironmentVariable("GITHUB_SHA"), real_unity_execution_performed = false,
    gpu_execution_performed = false, quest_execution_performed = false, latency_measured = false
};
var json = JsonSerializer.Serialize(report);
File.WriteAllText(args[1], json); Console.WriteLine(json);
return failed == 0 ? 0 : 1;
static int[] Integers(JsonElement values) => values.EnumerateArray().Select(value => value.GetInt32()).ToArray();
