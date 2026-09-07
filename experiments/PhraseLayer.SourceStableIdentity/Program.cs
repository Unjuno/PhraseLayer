using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Spatial;

const double associationDistance = 0.20;
const long retentionMicroseconds = 600_000;
var checks = new Dictionary<string, bool>();

// Isolate display changes from source normalization; the old experiment changed both at once.
var production = new WorldTextTrackStabilizer(associationDistance, 0.60, 0.10);
var originalId = SingleObserved(production.Update(Layout(Target("KEEP OFF", "立入禁止", 0.00)), 0)).TrackId;
var changed = production.Update(Layout(Target("KEEP OFF", "入らないでください", 0.01)), 100_000);
checks["production_display_only_change_creates_new_track"] = changed.Tracks.Count == 2 &&
    changed.Tracks.Any(track => track.TrackId == originalId && !track.ObservedThisFrame) &&
    changed.Tracks.Any(track => track.TrackId != originalId && track.ObservedThisFrame);
var normalizedProduction = new WorldTextTrackStabilizer(associationDistance, 0.60, 0.10);
var normalizedId = SingleObserved(normalizedProduction.Update(Layout(Target("KEEP OFF", "立入禁止", 0)), 0)).TrackId;
checks["production_case_spacing_only_change_preserves_track"] =
    SingleObserved(normalizedProduction.Update(Layout(Target("keep   off", "立入禁止", 0.01)), 100_000)).TrackId == normalizedId;

var candidate = new SourceStableIdentityPrototype(associationDistance, retentionMicroseconds);
var first = candidate.Update(new Observation("KEEP OFF", "立入禁止", 0), 0);
checks["candidate_display_only_change_preserves_identity"] =
    candidate.Update(new Observation("KEEP OFF", "入らないでください", 0.01), 100_000).Id == first.Id;
checks["candidate_case_spacing_only_change_preserves_identity"] =
    candidate.Update(new Observation("keep   off", "入らないでください", 0.01), 110_000).Id == first.Id;
checks["same_source_far_sign_is_separate"] = candidate.Update(new Observation("KEEP OFF", "立入禁止", 0.75), 200_000).Id != first.Id;
checks["changed_source_nearby_is_separate"] = candidate.Update(new Observation("KEEP OUT", "立入禁止", 0.015), 300_000).Id != first.Id;

// Independent trajectories test the exact inclusive retention boundary, not a renewed last-seen age.
var inclusive = new SourceStableIdentityPrototype(associationDistance, retentionMicroseconds);
var inclusiveId = inclusive.Update(new Observation("EXIT", "出口", 0), 0).Id;
checks["age_exactly_600000_us_is_retained"] = inclusive.Update(new Observation("EXIT", "出口", 0), 600_000).Id == inclusiveId;
var expired = new SourceStableIdentityPrototype(associationDistance, retentionMicroseconds);
var expiredId = expired.Update(new Observation("EXIT", "出口", 0), 0).Id;
checks["age_600001_us_is_expired"] = expired.Update(new Observation("EXIT", "出口", 0), 600_001).Id != expiredId;

// A frame is a batch. Verify one-to-one assignment in both observation orders.
foreach (var reverse in new[] { false, true })
{
    var batch = new SourceStableIdentityPrototype(associationDistance, retentionMicroseconds);
    var initial = batch.Update(new[] { new Observation("EXIT", "A", -0.15), new Observation("EXIT", "B", 0.15) }, 0);
    var observations = new[] { new Observation("EXIT", "A2", -0.04), new Observation("EXIT", "B2", 0.04) };
    if (reverse) Array.Reverse(observations);
    var next = batch.Update(observations, 100_000);
    checks["batch_one_to_one_" + (reverse ? "reversed" : "forward")] =
        next.Select(item => item.Id).Distinct().Count() == 2 &&
        next.Single(item => item.CenterX < 0).Id == initial[0].Id &&
        next.Single(item => item.CenterX > 0).Id == initial[1].Id;
}

// Preserve the original sequential-update counterexample instead of silently dropping it.
var sequential = new SourceStableIdentityPrototype(associationDistance, retentionMicroseconds);
var a0 = sequential.Update(new Observation("EXIT", "A", -0.15), 0);
var b0 = sequential.Update(new Observation("EXIT", "B", 0.15), 1_000);
var a1 = sequential.Update(new Observation("EXIT", "A2", -0.04), 100_000);
var b1 = sequential.Update(new Observation("EXIT", "B2", 0.04), 101_000);
var sequentialAliasing = a0.Id != b0.Id && a1.Id == a0.Id && b1.Id == a1.Id;
checks["sequential_aliasing_counterexample_reproduced"] = sequentialAliasing;

// Same observations, different ordering: a greedy assignment is not a global assignment solver.
var orderOutcomes = new List<string[]>();
foreach (var reverse in new[] { false, true })
{
    var ordered = new SourceStableIdentityPrototype(0.20, retentionMicroseconds);
    ordered.Update(new[] { new Observation("EXIT", "A", 0), new Observation("EXIT", "B", 0.10) }, 0);
    var observations = new[] { new Observation("EXIT", "A2", 0.06), new Observation("EXIT", "B2", 0.09) };
    if (reverse) Array.Reverse(observations);
    orderOutcomes.Add(ordered.Update(observations, 100_000).OrderBy(item => item.CenterX).Select(item => item.Id).ToArray());
}
var orderDependent = !orderOutcomes[0].SequenceEqual(orderOutcomes[1]);
checks["batch_order_dependence_counterexample_reproduced"] = orderDependent;

Console.WriteLine(JsonSerializer.Serialize(new
{
    status = checks.Values.All(value => value) ? "pass" : "fail",
    experiment = "source-stable-identity-characterization-v2",
    scope = "deterministic-core-geometry-prototype-only",
    checks,
    candidate_identity_inputs = new[] { "normalized_source_text", "one_dimensional_center_distance", "retention_age" },
    placement_kind_implemented = false,
    display_text_excluded_from_candidate_identity = true,
    association_distance_meters = associationDistance,
    retention_microseconds = retentionMicroseconds,
    sequential_aliasing_reproduced = sequentialAliasing,
    batch_order_dependence_reproduced = orderDependent,
    candidate_promotion = "rejected",
    rejection_reasons = new[] { "sequential-aliasing", "batch-order-dependence", "placement-kind-not-implemented", "one-dimensional-prototype" },
    product_integration_performed = false,
    camera_execution_performed = false,
    quest_execution_performed = false,
    latency_measured = false,
}));
if (checks.Values.Any(value => !value)) Environment.ExitCode = 1;

static WorldTextTrackState SingleObserved(WorldTextTrackingPlan plan) => plan.Tracks.Single(track => track.ObservedThisFrame);
static WorldTextLayoutPlan Layout(params WorldTextLayoutTarget[] targets) => new WorldTextLayoutPlan(targets);
static WorldTextLayoutTarget Target(string sourceText, string displayText, double centerX)
{
    var unit = new SemanticUnit("mwe:0:" + sourceText.Length, SemanticUnitKind.MultiwordExpression,
        0, sourceText.Length, sourceText, Math.Max(1, sourceText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length));
    var segment = new MixedLanguageSegment(sourceText, displayText, true, unit);
    var spatial = new SpatialAssistanceTarget(segment, Array.Empty<OcrTextRegionSpan>(),
        SpatialAssistanceCoverage.Exact, new ViewportEnvelope(0.2, 0.3, 0.4, 0.5));
    var ray = new SpatialRay(new SpatialVector3(0, 0, -1), new SpatialVector3(0, 0, 1));
    var hit = new SurfaceHit(new SpatialVector3(centerX, 0, 0), new SpatialVector3(0, 0, -1), 1.0);
    var projected = new ProjectedAssistanceTarget(spatial, OverlayPlacementKind.InPlaceReplacement,
        SpatialProjectionFailure.None, new ViewportPoint(0.3, 0.4), ray, hit);
    return new WorldTextLayoutTarget(projected, WorldTextLayoutFailure.None,
        new WorldTextSurface(new SpatialVector3(centerX, 0, 0), new SpatialVector3(1, 0, 0),
            new SpatialVector3(0, 1, 0), new SpatialVector3(0, 0, 1), 0.20, 0.05, 0.0));
}

sealed record Observation(string SourceText, string DisplayText, double CenterX);
sealed record IdentityResult(string Id, string NormalizedSource, double CenterX, long LastSeenMicroseconds);

// Deliberately retained baseline prototype. Counterexamples are evidence against promotion, not fixes.
sealed class SourceStableIdentityPrototype
{
    private readonly double maximumAssociationDistance;
    private readonly long retention;
    private readonly List<IdentityResult> active = new List<IdentityResult>();
    private int nextId = 1;
    private long? lastTimestamp;
    public SourceStableIdentityPrototype(double maximumAssociationDistance, long retentionMicroseconds)
    { this.maximumAssociationDistance = maximumAssociationDistance; retention = retentionMicroseconds; }
    public IdentityResult Update(Observation observation, long timestampMicroseconds) => Update(new[] { observation }, timestampMicroseconds).Single();
    public IReadOnlyList<IdentityResult> Update(IReadOnlyList<Observation> observations, long timestampMicroseconds)
    {
        if (lastTimestamp.HasValue && timestampMicroseconds < lastTimestamp.Value)
            throw new ArgumentException("Identity prototype timestamps must be non-decreasing.", nameof(timestampMicroseconds));
        lastTimestamp = timestampMicroseconds;
        active.RemoveAll(item => timestampMicroseconds - item.LastSeenMicroseconds > retention);
        var results = new List<IdentityResult>(observations.Count);
        var claimedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            var normalized = NormalizeSource(observation.SourceText);
            var candidate = active.Where(item => !claimedIds.Contains(item.Id) && item.NormalizedSource == normalized)
                .Select(item => new { Item = item, Distance = Math.Abs(item.CenterX - observation.CenterX) })
                .Where(pair => pair.Distance <= maximumAssociationDistance)
                .OrderBy(pair => pair.Distance).ThenBy(pair => pair.Item.Id, StringComparer.Ordinal).FirstOrDefault();
            IdentityResult result;
            if (candidate == null)
            {
                result = new IdentityResult("source-track-" + nextId++, normalized, observation.CenterX, timestampMicroseconds);
                active.Add(result);
            }
            else
            {
                result = candidate.Item with { CenterX = observation.CenterX, LastSeenMicroseconds = timestampMicroseconds };
                active[active.FindIndex(item => item.Id == result.Id)] = result;
            }
            claimedIds.Add(result.Id);
            results.Add(result);
        }
        return results;
    }
    private static string NormalizeSource(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        var builder = new StringBuilder();
        var previousWhitespace = false;
        foreach (var character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            { if (!previousWhitespace) builder.Append(' '); previousWhitespace = true; }
            else { builder.Append(char.ToUpperInvariant(character)); previousWhitespace = false; }
        }
        return builder.ToString();
    }
}
