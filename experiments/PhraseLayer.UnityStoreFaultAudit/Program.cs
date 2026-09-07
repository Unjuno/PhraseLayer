using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using PhraseLayer.Core.Learning;
using PhraseLayer.Unity;
using NativeFile = System.IO.File;
using IoProbe = PhraseLayer.Unity.File;

var reports = new List<object>();
var violations = 0;
var faultCases = 0;
foreach (var initialState in new[] { "primary-only", "backup-only", "primary-plus-backup" })
{
    var control = RunCase(initialState, null, "none");
    reports.Add(control);
    if (!control.Safe) violations++;
    for (var point = 1; point <= control.Trace.Length; point++)
    {
        foreach (var timing in new[] { "before", "after" })
        {
            var result = RunCase(initialState, point, timing);
            reports.Add(result);
            faultCases++;
            if (!result.Safe) violations++;
        }
    }
}
var sourcePath = "unity/PhraseLayer.Unity/Assets/Scripts/UnityLearnerProfileStore.cs";
Console.WriteLine(JsonSerializer.Serialize(new
{
    experiment = "unity-profile-store-source-fault-injection",
    scope = "actual-store-source-with-host-json-and-file-facades",
    experiment_status = "completed",
    recovery_result = violations == 0 ? "PASS" : "FAIL",
    control_cases = 3, injected_fault_cases = faultCases, violations,
    store_source_sha256 = Convert.ToHexString(SHA256.HashData(NativeFile.ReadAllBytes(sourcePath))).ToLowerInvariant(),
    reports,
    real_temporary_files_used = true,
    injected_boundaries = "before-and-after-successful-path-write-delete-move-calls",
    unity_json_implementation_used = false,
    unity_editor_executed = false,
    real_process_crash_injected = false,
    fsync_or_power_loss_durability_measured = false,
    quest_execution_performed = false,
    latency_measured = false,
}));
if (args.Contains("--require-filesystem") && violations != 0) Environment.ExitCode = 1;

static CaseResult RunCase(string initialState, int? faultPoint, string timing)
{
    var directory = Path.Combine(Path.GetTempPath(), "phraselayer-store-fault-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    IoProbe.Disarm();
    try
    {
        var path = Path.Combine(directory, "profile.json");
        var store = new UnityLearnerProfileStore(path);
        var old = new LearnerProfileSnapshot(0.5, new[] { new LearnerKnowledgeEntry("alpha", 0.4) });
        var next = new LearnerProfileSnapshot(0.5, new[] { new LearnerKnowledgeEntry("alpha", 0.9) });
        store.Save(old);
        if (initialState == "backup-only") NativeFile.Move(path, store.BackupPath);
        if (initialState == "primary-plus-backup") NativeFile.Copy(path, store.BackupPath);
        if (store.Load().Entries.Single().Understanding != 0.4) throw new InvalidOperationException("Old snapshot setup did not round-trip.");
        IoProbe.Arm(faultPoint, timing);
        var threw = false;
        try { store.Save(next); } catch (InjectedFileFailure) { threw = true; }
        var trace = IoProbe.Trace.ToArray();
        var injected = IoProbe.FaultInjected;
        IoProbe.Disarm();
        var recovered = store.Load();
        double? recoveredScore = recovered == null ? (double?)null : recovered.Entries.Single().Understanding;
        var oldOrNew = recoveredScore == 0.4 || recoveredScore == 0.9;
        var safe = faultPoint.HasValue ? threw && injected && oldOrNew : !threw && recoveredScore == 0.9;
        return new CaseResult(initialState, faultPoint, timing, safe, injected, recoveredScore,
            NativeFile.Exists(path), NativeFile.Exists(store.BackupPath), NativeFile.Exists(store.TemporaryPath), trace);
    }
    finally
    {
        IoProbe.Disarm();
        Directory.Delete(directory, recursive: true);
    }
}
sealed record CaseResult(string InitialState, int? FaultPoint, string Timing, bool Safe, bool FaultInjected,
    double? RecoveredScore, bool PrimaryExists, bool BackupExists, bool TemporaryExists, string[] Trace);
