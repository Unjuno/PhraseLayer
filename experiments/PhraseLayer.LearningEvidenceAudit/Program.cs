using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Translation;

var invalidScores = new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity };
var document = new RuleBasedSemanticSegmenter().Segment("alpha beta.");
var words = document.OfKind(SemanticUnitKind.Word).ToArray();
var checks = new Dictionary<string, bool>();
foreach (var invalid in invalidScores)
{
    var name = double.IsNaN(invalid) ? "NaN" : invalid > 0 ? "positive_infinity" : "negative_infinity";
    checks["default_rejects_" + name] = Rejects(() => new InMemoryLearnerModel(invalid));
    checks["estimate_rejects_" + name] = Rejects(() => new KnowledgeEstimate(words[0], invalid, false));
    var model = new InMemoryLearnerModel(0.5);
    model.SetUnderstanding("alpha", 0.4);
    var rejected = Rejects(() => model.SetUnderstanding("alpha", invalid));
    checks["setter_rejects_" + name + "_without_mutation"] = rejected && model.Estimate(words[0]).Understanding == 0.4;
}
checks["estimate_rejects_null_unit"] = Rejects(() => new KnowledgeEstimate(null!, 0.5, false));
checks["finite_out_of_range_scores_keep_clamping_contract"] =
    new InMemoryLearnerModel(-0.1).DefaultUnderstanding == 0 && new InMemoryLearnerModel(1.1).DefaultUnderstanding == 1;

var learner = new InMemoryLearnerModel(0.5);
var plan = await BuildPipeline(learner, new EmptyTranslator()).PlanAsync(document.SourceText, AssistancePolicy.ForMode(AssistanceMode.Auto));
var session = new LearningEncounterSession(plan, new LearnerAdaptationEngine(learner));
session.Record(words[0], LearningEvidenceKind.RecallSucceeded);
var invalidRejectedAtRecord = Rejects(() => session.Record(words[1], (LearningEvidenceKind)int.MaxValue));
var before = learner.Estimate(words[0]).Understanding;
var failedFinishes = 0;
var afterFirst = before;
var afterRetry = before;
if (!invalidRejectedAtRecord)
{
    try { session.Finish(); } catch (ArgumentOutOfRangeException) { failedFinishes++; }
    afterFirst = learner.Estimate(words[0]).Understanding;
    try { session.Finish(); } catch (ArgumentOutOfRangeException) { failedFinishes++; }
    afterRetry = learner.Estimate(words[0]).Understanding;
}
else
{
    var summary = session.Finish();
    afterFirst = learner.Estimate(words[0]).Understanding;
    checks["valid_finish_is_idempotent"] = ReferenceEquals(summary, session.Finish()) &&
        learner.Estimate(words[0]).Understanding == afterFirst;
}
checks["invalid_evidence_rejected_before_finish"] = invalidRejectedAtRecord;
checks["record_never_applies_learning"] = before == 0.5;

// Characterize two further concerns separately; these are not the input-safety acceptance criteria.
var fallbackLearner = new InMemoryLearnerModel(0.55);
var fallback = await BuildPipeline(fallbackLearner, new EmptyTranslator()).PlanAsync("alpha.", AssistancePolicy.ForMode(AssistanceMode.Auto));
var fallbackWord = fallback.Document!.OfKind(SemanticUnitKind.Word).Single();
var fallbackBefore = fallbackLearner.Estimate(fallbackWord).Understanding;
var fallbackSummary = new LearningEncounterSession(fallback, new LearnerAdaptationEngine(fallbackLearner)).Finish();
var repetitions = new List<object>();
foreach (var count in new[] { 1, 2, 4, 8, 16, 32 })
{
    var source = string.Join(" ", Enumerable.Repeat("go", count)) + ".";
    var doc = new RuleBasedSemanticSegmenter().Segment(source);
    var model = new InMemoryLearnerModel(0.5);
    var unassisted = new MixedLanguagePlan(source,
        new[] { new MixedLanguageSegment(source, source, false, null) },
        new AssistancePlan(Array.Empty<AssistanceDecision>(), 0, 0), doc);
    var result = new LearningEncounterSession(unassisted, new LearnerAdaptationEngine(model)).Finish(true);
    var estimate = model.Estimate(doc.OfKind(SemanticUnitKind.Word).First());
    repetitions.Add(new { occurrences = count, updates = result.Updates.Count, understanding = estimate.Understanding, state = estimate.State.ToString() });
}
var safe = checks.Values.All(value => value);
Console.WriteLine(JsonSerializer.Serialize(new
{
    experiment = "learning-evidence-input-and-attribution-audit",
    scope = "deterministic-core-only",
    experiment_status = "completed",
    input_safety_result = safe ? "PASS" : "FAIL",
    checks,
    invalid_evidence = new { invalid_rejected_at_record = invalidRejectedAtRecord, failed_finishes = failedFinishes,
        score_before = before, score_after_first_finish = afterFirst, score_after_retry = afterRetry },
    empty_translation = new { source_preserved = fallback.DisplayText == fallback.SourceText,
        planned_assisted_segments = fallback.Segments.Count(item => item.IsAssisted), updates = fallbackSummary.Updates.Count,
        score_before = fallbackBefore, score_after = fallbackLearner.Estimate(fallbackWord).Understanding },
    repeated_lexeme_one_encounter = repetitions,
    framework = RuntimeInformation.FrameworkDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    human_learning_effectiveness_measured = false,
    quest_execution_performed = false,
    latency_measured = false,
}));
if (args.Contains("--require-safe") && !safe) Environment.ExitCode = 1;

static bool Rejects(Action action)
{
    try { action(); return false; } catch (ArgumentException) { return true; }
}
static LanguagePipeline BuildPipeline(InMemoryLearnerModel learner, ITranslationEngine translator) =>
    new LanguagePipeline(new RuleBasedSemanticSegmenter(), learner, new AssistancePlanner(), translator);
sealed class EmptyTranslator : ITranslationEngine
{
    public Task<string> TranslateAsync(string sourceText, string context, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(string.Empty); }
}
