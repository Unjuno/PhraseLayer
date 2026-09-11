using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PhraseLayer.Core.Inputs;

if (args.Length != 2)
    throw new ArgumentException("Expected detector replay manifest path and output report path.");

var manifestPath = Path.GetFullPath(args[0]);
var manifestDirectory = Path.GetDirectoryName(manifestPath) ?? throw new InvalidOperationException();
using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
var root = manifest.RootElement;
var boxThreshold = root.TryGetProperty("box_threshold", out var configuredBoxThreshold)
    ? configuredBoxThreshold.GetDouble()
    : PaddleDbPostprocessSpec.V6TinyBoxThreshold;
var minimumShortSide = root.TryGetProperty("minimum_short_side", out var configuredMinimumShortSide)
    ? configuredMinimumShortSide.GetDouble()
    : PaddleDbPostprocessSpec.DefaultMinimumShortSide;
var processor = new PaddleDbQuadPostprocessor(new PaddleDbPostprocessSpec(
    PaddleDbPostprocessSpec.V6TinyBitmapThreshold,
    boxThreshold,
    PaddleDbPostprocessSpec.V6TinyMaxCandidates,
    PaddleDbPostprocessSpec.V6TinyUnclipRatio,
    PaddleDbScoreMode.Fast,
    minimumShortSide));
var rows = new List<object>();

foreach (var item in root.GetProperty("cases").EnumerateArray())
{
    var name = item.GetProperty("name").GetString() ?? throw new InvalidDataException("Case name missing.");
    var width = item.GetProperty("width").GetInt32();
    var height = item.GetProperty("height").GetInt32();
    var destinationWidth = item.GetProperty("destination_width").GetInt32();
    var destinationHeight = item.GetProperty("destination_height").GetInt32();
    var relativeMapPath = item.GetProperty("map_file").GetString() ?? throw new InvalidDataException("Map path missing.");
    var mapPath = Path.GetFullPath(Path.Combine(manifestDirectory, relativeMapPath));
    if (!mapPath.StartsWith(manifestDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidDataException("Probability map path escapes replay directory.");

    var expectedBytes = checked(width * height * sizeof(float));
    var raw = File.ReadAllBytes(mapPath);
    if (raw.Length != expectedBytes)
        throw new InvalidDataException($"{name}: probability map byte count mismatch.");

    var values = new float[width * height];
    Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
    var detections = processor.Process(
        new PaddleDbProbabilityMap(values, width, height),
        destinationWidth,
        destinationHeight);

    rows.Add(new
    {
        name,
        detections = detections.Select(detection => new
        {
            score = detection.Score,
            points = detection.ImageBounds.Points.Select(point => new[] { point.X, point.Y }).ToArray(),
        }).ToArray(),
    });
}

var report = new
{
    experiment = "production-core-db-quad-postprocess-replay",
    configured_box_threshold = boxThreshold,
    configured_minimum_short_side = minimumShortSide,
    cases = rows,
    real_unity_execution_performed = false,
    gpu_execution_performed = false,
    quest_execution_performed = false,
    latency_measured = false,
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1])) ?? ".");
File.WriteAllText(args[1], JsonSerializer.Serialize(report));
Console.WriteLine(JsonSerializer.Serialize(new { report.experiment, replay_cases = rows.Count, box_threshold = boxThreshold }));
