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

var production = new WorldTextTrackStabilizer(
    maximumAssociationDistanceMeters: associationDistance,
    retentionSeconds: retentionMicroseconds / 1_000_000.0,
    smoothingTimeConstantSeconds: 0.10);
var candidate = new SourceStableIdentityPrototype(associationDistance, retentionMicroseconds);

var firstLayout = Layout(Target("KEEP OFF", "立入禁止", 0.00));
var changedDisplayLayout = Layout(Target("keep   off", "入らないでください", 0.01));
var productionFirstId = SingleObserved(production.Update(firstLayout, 0)).TrackId;
var productionAfterDisplayChange = production.Update(changedDisplayLayout, 100_000);
var productionDisplayChangeCreatedNewTrack =
    productionAfterDisplayChange.Tracks.Count == 2 &&
    productionAfterDisplayChange.Tracks.Any(track => track.TrackId == productionFirstId && !track.ObservedThisFrame) &&
    productionAfterDisplayChange.Tracks.Any(track => track.TrackId != productionFirstId && track.ObservedThisFrame);
if (!productionDisplayChangeCreatedNewTrack)
    throw new InvalidOperationException("Baseline no longer demonstrates display-sensitive WorldTextTrack identity.");

var candidateFirst = candidate.Update(new Observation("KEEP OFF", "立入禁止", 0.00), 0);
var candidateDisplayChanged = candidate.Update(new Observation("keep   off", "入らないでください", 0.01), 100_000);
if (candidateDisplayChanged.Id != candidateFirst.Id)
    throw new InvalidOperationException("Source-stable candidate changed identity when only normalized case/spacing/display text changed.");

var sameSourceFar = candidate.Update(new Observation("KEEP OFF", "立入禁止", 0.75), 200_000);
if (sameSourceFar.Id == candidateFirst.Id)
    throw new InvalidOperationException("Source-stable candidate merged physically separate identical signs.");

var changedSourceNearby = candidate.Update(new Observation("KEEP OUT", "立入禁止", 0.015), 300_000);
if (changedSourceNearby.Id == candidateFirst.Id)
    throw new InvalidOperationException("Source-stable candidate reused identity after source text changed.");

var beforeExpiry = candidate.Update(new Observation("KEEP OFF", "別表示", 0.02), 599_999);
if (beforeExpiry.Id != candidateFirst.Id)
    throw new InvalidOperationException("Source-stable candidate expired before the reviewed retention boundary.");

candidate.Update(Array.Empty<Observation>(), 1_200_000);
var afterExpiry = candidate.Update(new Observation("KEEP OFF", "立入禁止", 0.02), 1_200_001);
if (afterExpiry.Id == candidateFirst.Id)
    throw new InvalidOperationException("Source-stable candidate reused identity after retention expiry.");

var duplicates = new SourceStableIdentityPrototype(associationDistance, retentionMicroseconds);
var left = duplicates.Update(new Observation("EXIT", "出口", 0.00), 0);
var right = duplicates.Update(new Observation("EXIT", "出口", 0.50), 10_000);
var leftAgain = duplicates.Update(new Observation("exit", "でぐち", 0.03), 20_000);
if (left.Id == right.Id || leftAgain.Id != left.Id)
    throw new InvalidOperationException("Identical-source multi-sign association failed physical disambiguation.");

var crossing = new SourceStableIdentityPrototype(associationDistance, retentionMicroseconds);
var a0 = crossing.Update(new Observation("EXIT", "出口A", -0.15), 0);
var b0 = crossing.Update(new Observation("EXIT", "出口B", 0.15), 1_000);
var a1 = crossing.Update(new Observation("EXIT", "出口A2", -0.04), 100_000);
var b1 = crossing.Update(new Observation("EXIT", "出口B2", 0.04), 101_000);
if (a1.Id != a0.Id || b1.Id != b0.Id)
    throw new InvalidOperationException("Nearest-neighbor source identity lost distinct nearby identical signs before crossing.");

Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "pass",
    experiment = "source-stable-physical-identity-prototype",
    scope = "deterministic-core-geometry-prototype-only",
    production_world_text_track_display_sensitive = productionDisplayChangeCreatedNewTrack,
    candidate_identity_inputs = new[] { "normalized_source_text", "placement_kind", "physical_center_distance", "retention_age" },
    display_text_excluded_from_identity = true,
    source_normalization = "trim-collapse-whitespace-invariant-case",
    same_source_display_change_preserves_identity = true,
    same_source_far_sign_separates_identity = true,
    changed_source_nearby_resets_identity = true,
    retention_expiry_resets_identity = true,
    identical_source_multiple_signs_spatially_disambiguated = true,
    candidate_safe_for_cross_source_hysteresis = false,
    global_text_only_identity_allowed = false,
    product_integration_performed = false,
    camera_execution_performed = false,
    quest_execution_performed = false,
}));

static WorldTextTrack SingleObserved(WorldTextTrackingPlan plan)
{
    var observed = plan.Tracks.Where(track => track.ObservedThisFrame).ToArray();
    if (observed.Length != 1) throw new InvalidOperationException("Expected exactly one observed world-text track.");
    return observed[0];
}

static WorldTextLayoutPlan Layout(params WorldTextLayoutTarget[] targets) => new WorldTextLayoutPlan(targets);

static WorldTextLayoutTarget Target(string sourceText, string displayText, double centerX)
{
    var unit = new SemanticUnit(
        "mwe:0:" + sourceText.Length,
        SemanticUnitKind.MultiwordExpression,
        0,
        sourceText.Length,
        sourceText,
        Math.Max(1, sourceText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length));
    var segment = new MixedLanguageSegment(sourceText, displayText, true, unit);
    var spatial = new SpatialAssistanceTarget(
        segment,
        Array.Empty<OcrTextRegionSpan>(),
        SpatialAssistanceCoverage.Exact,
        new ViewportEnvelope(0.2, 0.3, 0.4, 0.5));
    var ray = new SpatialRay(new SpatialVector3(0, 0, -1), new SpatialVector3(0, 0, 1));
    var hit = new SurfaceHit(new SpatialVector3(centerX, 0, 0), new SpatialVector3(0, 0, -1), 1.0);
    var projected = new ProjectedAssistanceTarget(
        spatial,
        OverlayPlacementKind.InPlaceReplacement,
        SpatialProjectionFailure.None,
        new ViewportPoint(0.3, 0.4),
        ray,
        hit);
    return new WorldTextLayoutTarget(
        projected,
        WorldTextLayoutFailure.None,
        new WorldTextSurface(
            new SpatialVector3(centerX, 0, 0),
            new SpatialVector3(1, 0, 0),
            new SpatialVector3(0, 1, 0),
            new SpatialVector3(0, 0, 1),
            0.20,
            0.05,
            0.0));
}

sealed record Observation(string SourceText, string DisplayText, double CenterX);
sealed record IdentityResult(string Id, string NormalizedSource, double CenterX, long LastSeenMicroseconds);

sealed class SourceStableIdentityPrototype
{
    private readonly double maximumAssociationDistance;
    private readonly long retention;
    private readonly List<IdentityResult> active = new List<IdentityResult>();
    private int nextId = 1;
    private long? lastTimestamp;

    public SourceStableIdentityPrototype(double maximumAssociationDistance, long retentionMicroseconds)
    {
        this.maximumAssociationDistance = maximumAssociationDistance;
        retention = retentionMicroseconds;
    }

    public IdentityResult Update(Observation observation, long timestampMicroseconds)
    {
        return Update(new[] { observation }, timestampMicroseconds).Single();
    }

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
            var candidate = active
                .Where(item => !claimedIds.Contains(item.Id) && item.NormalizedSource == normalized)
                .Select(item => new { Item = item, Distance = Math.Abs(item.CenterX - observation.CenterX) })
                .Where(pair => pair.Distance <= maximumAssociationDistance)
                .OrderBy(pair => pair.Distance)
                .ThenBy(pair => pair.Item.Id, StringComparer.Ordinal)
                .FirstOrDefault();

            IdentityResult result;
            if (candidate == null)
            {
                result = new IdentityResult("source-track-" + nextId++, normalized, observation.CenterX, timestampMicroseconds);
                active.Add(result);
            }
            else
            {
                result = candidate.Item with { CenterX = observation.CenterX, LastSeenMicroseconds = timestampMicroseconds };
                var index = active.FindIndex(item => item.Id == result.Id);
                active[index] = result;
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
            {
                if (!previousWhitespace) builder.Append(' ');
                previousWhitespace = true;
            }
            else
            {
                builder.Append(char.ToUpperInvariant(character));
                previousWhitespace = false;
            }
        }
        return builder.ToString();
    }
}
