using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;

const string source = "alpha beta.";
var doc = new RuleBasedSemanticSegmenter().Segment(source);
var words = doc.OfKind(SemanticUnitKind.Word).ToArray();
var plan = new MixedLanguagePlan(source, new[] { new MixedLanguageSegment(source, source, false, null) },
    new AssistancePlan(Array.Empty<AssistanceDecision>(), 0, 0), doc);
var reports = new List<object>();
var allSafe = true;
foreach (var scenario in new[] { "no-fault", "throw-before-first-save", "commit-then-throw-first-save" })
{
    var store = new FaultingStore(new InMemoryLearnerModel(0.5).CreateSnapshot(), scenario);
    var learner = new PersistentLearnerModel(store);
    var session = new LearningEncounterSession(plan, new LearnerAdaptationEngine(learner));
    var firstThrew = false;
    try { session.Finish(true); } catch (InjectedStoreException) { firstThrew = true; }
    var afterFirst = words.Select(word => learner.Estimate(word).Understanding).ToArray();
    var diskAfterFirstModel = InMemoryLearnerModel.FromSnapshot(store.Stored);
    var diskAfterFirst = words.Select(word => diskAfterFirstModel.Estimate(word).Understanding).ToArray();
    var summary = session.Finish(true);
    var final = words.Select(word => learner.Estimate(word).Understanding).ToArray();
    var disk = InMemoryLearnerModel.FromSnapshot(store.Stored);
    var saveCount = store.SaveCalls;
    var repeated = session.Finish(true);
    var checks = new Dictionary<string, bool>
    {
        ["fault_injected_as_requested"] = firstThrew == (scenario != "no-fault"),
        ["failed_commit_did_not_mutate_memory"] = !firstThrew || afterFirst.All(value => Math.Abs(value - 0.5) < 1e-12),
        ["retry_applied_each_evidence_once"] = final.All(value => Math.Abs(value - 0.54) < 1e-12),
        ["durable_and_memory_state_agree_after_success"] = words.All(word => Math.Abs(learner.Estimate(word).Understanding - disk.Estimate(word).Understanding) < 1e-12),
        ["successful_finish_is_idempotent"] = ReferenceEquals(summary, repeated) && saveCount == store.SaveCalls,
        ["one_snapshot_write_per_attempt"] = saveCount == (firstThrew ? 2 : 1),
    };
    var safe = checks.Values.All(value => value);
    allSafe &= safe;
    reports.Add(new { scenario, result = safe ? "PASS" : "FAIL", checks, after_first_attempt_memory = afterFirst,
        after_first_attempt_durable = diskAfterFirst, after_retry_memory = final, save_calls = saveCount });
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    experiment = "learning-commit-fault-injection",
    scope = "deterministic-in-memory-store-double-only",
    experiment_status = "completed",
    transaction_safety_result = allSafe ? "PASS" : "FAIL",
    scenarios = reports,
    framework = RuntimeInformation.FrameworkDescription,
    real_filesystem_tested = false,
    concurrent_threads_tested = false,
    process_crash_tested = false,
    human_learning_effectiveness_measured = false,
    quest_execution_performed = false,
    latency_measured = false,
}));
if (args.Contains("--require-transaction") && !allSafe) Environment.ExitCode = 1;

sealed class InjectedStoreException : Exception { }
sealed class FaultingStore : ILearnerProfileStore
{
    private readonly string scenario;
    public FaultingStore(LearnerProfileSnapshot initial, string scenario) { Stored = initial; this.scenario = scenario; }
    public LearnerProfileSnapshot Stored { get; private set; }
    public int SaveCalls { get; private set; }
    public LearnerProfileSnapshot? Load() => Stored;
    public void Save(LearnerProfileSnapshot snapshot)
    {
        SaveCalls++;
        if (SaveCalls == 1 && scenario != "no-fault")
        {
            if (scenario == "commit-then-throw-first-save") Stored = snapshot;
            throw new InjectedStoreException();
        }
        Stored = snapshot;
    }
}
