using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PhraseLayer.Core.Inputs;

if (args.Length != 2) throw new ArgumentException("Expected replay JSON path and report JSON path.");
var checks = new List<object>(); var failed = 0;
var bytes = File.ReadAllBytes(args[0]);
using var document = JsonDocument.Parse(bytes);
var root = document.RootElement;
var dictionary = root.GetProperty("dictionary").EnumerateArray().Select(value => value.GetString() ?? throw new InvalidDataException()).ToArray();
var realCases = root.GetProperty("real_model_cases").GetInt32();
if (realCases <= 0) throw new InvalidDataException("No actual model inference cases were supplied.");
foreach (var item in root.GetProperty("cases").EnumerateArray())
{
    var name = item.GetProperty("name").GetString();
    bool passed; string? error = null;
    try
    {
        var shape = Integers(item.GetProperty("probability_shape"));
        var packedShape = Integers(item.GetProperty("packed_shape"));
        var packedValues = item.GetProperty("packed_values").EnumerateArray().Select(value => value.GetSingle()).ToArray();
        var unpacked = PaddleCtcPackedOutput.Unpack(shape, packedShape, packedValues);
        PaddleOcrRuntimeContract.ValidateRecognizerReduced(shape, unpacked.ClassIndices, unpacked.MaxScores, dictionary.Length);
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
if (checks.Count < realCases) throw new InvalidDataException("Replay has fewer cases than the model observation count.");
var report = new {
    experiment = "actual-onnx-packed-output-through-production-core-decoder", safety_result = failed == 0 ? "PASS" : "FAIL",
    real_model_cases = realCases, total_replay_cases = checks.Count, failed_checks = failed, checks,
    replay_sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
    csharp_core_replay_performed = true, actual_model_executed_in_preceding_python_step = true,
    framework = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    git_commit = Environment.GetEnvironmentVariable("GITHUB_SHA"), real_unity_execution_performed = false,
    gpu_execution_performed = false, quest_execution_performed = false, latency_measured = false
};
var json = JsonSerializer.Serialize(report);
File.WriteAllText(args[1], json); Console.WriteLine(json);
return failed == 0 ? 0 : 1;
static int[] Integers(JsonElement values) => values.EnumerateArray().Select(value => value.GetInt32()).ToArray();
