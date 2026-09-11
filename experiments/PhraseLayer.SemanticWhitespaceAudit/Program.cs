using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Translation;

var checks = new List<object>(); var failures = 0;
var expressions = new[] { "keep off", "emergency exit", "in front of" };
var separators = new[] { " ", "  ", "\t", "\n", "\r\n", "\u00a0", "\u2003", "\u202f", " \t " };
foreach (var expression in expressions)
    for (var separatorIndex = 0; separatorIndex < separators.Length; separatorIndex++)
        for (var casing = 0; casing < 3; casing++)
        {
            var text = casing == 0 ? expression : casing == 1 ? expression.ToUpperInvariant() :
                string.Concat(expression.Select((character, index) => index % 2 == 0 ? char.ToUpperInvariant(character) : character));
            var body = string.Join(separators[separatorIndex], text.Split(' '));
            var name = expression + ":whitespace=" + separatorIndex + ":case=" + casing;
            await Check(name, async () =>
            {
                const string prefix = "Please read "; const string suffix = " slowly.";
                var source = prefix + body + suffix;
                var segmenter = new RuleBasedSemanticSegmenter(new[] { expression });
                var document = segmenter.Segment(source);
                var mwes = document.OfKind(SemanticUnitKind.MultiwordExpression).ToArray();
                if (mwes.Length != 1 || mwes[0].Text != body || mwes[0].Start != prefix.Length || mwes[0].Length != body.Length)
                    return false;
                if (mwes[0].TokenCount != expression.Split(' ').Length) return false;
                if (source.Substring(mwes[0].Start, mwes[0].Length) != mwes[0].Text) return false;
                var learner = new InMemoryLearnerModel(0.95); learner.SetUnderstanding(expression, 0.10);
                var pipeline = new LanguagePipeline(segmenter, learner, new AssistancePlanner(),
                    new DictionaryTranslationEngine(new Dictionary<string, string> { [expression] = "仮訳" }));
                var plan = await pipeline.PlanAsync(source, AssistancePolicy.ForMode(AssistanceMode.Balanced));
                if (plan.DisplayText != prefix + "仮訳" + suffix || plan.Assistance.Decisions.Count != 1 ||
                    plan.Assistance.Decisions[0].Unit.Kind != SemanticUnitKind.MultiwordExpression) return false;
                learner.SetUnderstanding(expression, 0.99);
                var known = await pipeline.PlanAsync(source, AssistancePolicy.ForMode(AssistanceMode.Balanced));
                return known.DisplayText == source && known.Assistance.Decisions.Count == 0 && plan.DisplayText == prefix + "仮訳" + suffix;
            });
        }
foreach (var source in new[] { "keep offshore", "keep off-road", "keep off's", "keep off’s", "road-keep off", "keeper off", "keep off7", "keep, off" })
    await Check("never_split_atomic_token:" + source, () => Task.FromResult(!new RuleBasedSemanticSegmenter(new[] { "keep off" }).Segment(source).OfKind(SemanticUnitKind.MultiwordExpression).Any()));
await Check("longest_expression_preserves_nonoverlap", () => {
    var units = new RuleBasedSemanticSegmenter(new[] { "in front", "front of", "in front of" }).Segment("stand in\tfront\tof us").OfKind(SemanticUnitKind.MultiwordExpression).ToArray();
    return Task.FromResult(units.Length == 1 && InMemoryLearnerModel.Normalize(units[0].Text) == "in front of");
});
await Check("configured_compound_token_is_preserved", () => {
    var units = new RuleBasedSemanticSegmenter(new[] { "state-of-the-art model" }).Segment("the state-of-the-art\tmodel works").OfKind(SemanticUnitKind.MultiwordExpression).ToArray();
    return Task.FromResult(units.Length == 1 && units[0].TokenCount == 2);
});
await Check("adjacent_occurrences_keep_distinct_offsets", () => {
    var source = "keep\toff keep\noff";
    var units = new RuleBasedSemanticSegmenter(new[] { "keep off" }).Segment(source).OfKind(SemanticUnitKind.MultiwordExpression).ToArray();
    return Task.FromResult(units.Length == 2 && !units[0].Overlaps(units[1]) && units.All(unit => source.Substring(unit.Start, unit.Length) == unit.Text));
});
await Check("invalid_compound_does_not_hide_later_valid_occurrence", () => {
    const string source = "keep off-road keep off";
    var units = new RuleBasedSemanticSegmenter(new[] { "keep off" }).Segment(source).OfKind(SemanticUnitKind.MultiwordExpression).ToArray();
    return Task.FromResult(units.Length == 1 && units[0].Start == source.LastIndexOf("keep off", StringComparison.Ordinal));
});
var json = JsonSerializer.Serialize(new {
    experiment = "semantic-whitespace-token-boundary-and-learning-key-parity", experiment_status = "completed",
    safety_result = failures == 0 ? "PASS" : "FAIL", failed_checks = failures, checks,
    whitespace_casing_cells = expressions.Length * separators.Length * 3,
    scope = "actual-Core-segmenter-pipeline-learner-and-dictionary-translation-on-synthetic-text",
    text_offsets_are_utf16_code_units = true, character_percentage_selection_used = false,
    git_commit = Environment.GetEnvironmentVariable("GITHUB_SHA"), framework = RuntimeInformation.FrameworkDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(), actual_ocr_model_executed = false,
    real_unity_execution_performed = false, quest_execution_performed = false, human_learning_effectiveness_measured = false
});
Console.WriteLine(json);
var path = Environment.GetEnvironmentVariable("AUDIT_REPORT_PATH"); if (!string.IsNullOrEmpty(path)) System.IO.File.WriteAllText(path, json);
return args.Contains("--require-safe") && failures != 0 ? 1 : 0;
async Task Check(string name, Func<Task<bool>> test) {
    bool passed; string? error = null;
    try { passed = await test(); } catch (Exception exception) { passed = false; error = exception.GetType().Name; }
    checks.Add(new { name, passed, error_type = error }); if (!passed) failures++;
}
