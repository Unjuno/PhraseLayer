# Profile-store fault injection

## Baseline measurement

Probe commit `37eb01017ace83f39788bd9ca30c5270ff220bb1`.
Experimental Audit run `34144549188`, job `101813619777`, PR merge checkout `69ad6cef1a4df50a540f07e7e11e562261f4e894`.
Store source SHA-256: `1c260ebd4e433ac0614f38d39d7e51860c9196158360c68b05ecc1517ea5deb3`.
Ubuntu 24.04.4 x64, SDK 10.0.400, .NET 8 target, Release. The actual store source is linked into a host console project with `UNITY_5_3_OR_NEWER` enabled. JSON and File test facades are explicitly substituted; nonfaulting file operations delegate to real host temporary files. Unity Editor and actual Unity JsonUtility are not executed.

Three initial states were tested: primary only, backup only, primary plus backup. For each successful-path mutating file call, a single exception was injected immediately before or after that call. This produced 3 no-fault controls and 24 fault cases.

Result: **2 recovery violations**. Both began with only the backup present. The previous code deleted that sole valid copy before moving temp to primary. An exception immediately after deletion or immediately before promotion left neither primary nor backup. Load returned null despite a previously stored profile.

## Fix and executable gate

Delete an existing backup before rotation only inside the branch where a primary exists. In a backup-only state, retain backup until promotion succeeds. The existing recovery branch can then restore the backup if promotion throws.

Run:

```sh
dotnet run --project experiments/PhraseLayer.UnityStoreFaultAudit/PhraseLayer.UnityStoreFaultAudit.csproj -c Release -- --require-filesystem
```

The acceptance criterion is zero recovery violations, with every requested injection reached and a valid old or new snapshot recoverable. No-fault controls must return the new snapshot. The workflow now enforces this criterion; results for the fix must be read from its own run.

## Limits

Only one injected exception at a time is tested, at enumerated WriteAllText/Delete/Move boundaries. Partial writes inside the OS, simultaneous failures during backup restoration, actual process termination, fsync, storage corruption, power loss and concurrent writers are not tested. The JSON facade is for synthetic valid DTOs and is not a Unity serialization parity test. These results do not establish Quest persistence reliability or latency.
