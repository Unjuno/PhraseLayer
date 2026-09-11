# Autonomous experiment audit: 2026-09-08 (JST)

## Scope and baseline

Repository baseline: `6981733b443f31046e8d583843b8f3e0eb7c8396`.
The existing identity probe failed to compile because `WorldTextTrack` does not exist; the public type is `WorldTextTrackState`.
The probe was repaired without changing the production tracker. Its previous sequential sign-association claim is now tested as a counterexample, not silently removed.
The prototype does not implement placement-kind identity and is one-dimensional. Those limitations are explicit and promotion remains rejected.

## Measured learning evidence baseline

Probe commit: `89adfd50f76fc4a9d14da0cfbb95faf19e0f38cc`.
GitHub Actions Experimental Audit run `34142826019`, job `101808390649`.
The PR merge checkout was `4b8c0e2921a7b419dd6161953793e27568acf204`.
Execution: Ubuntu 24.04.4, x64, selected SDK 10.0.400, runtime .NET 8.0.30, Release.
This is a deterministic behavior experiment, not a latency benchmark; CPU model/clock were not recorded and no hardware speed claim is permitted.

Observed input-safety result: **FAIL**. Nine non-finite entry-point checks, null-unit validation, and early invalid-event rejection failed. The finite clamping and deferred-mutation controls passed.

A pending valid recall for `alpha` followed by an invalid event for `beta` caused two failed finishes. The `alpha` score changed from 0.5 to 0.6 and then 0.6799999999999999 on retry. This is partial application plus replay, not new learning evidence.
An empty translator preserved `alpha.` but generated one automatic exposure update: 0.55 to 0.5575.
One successful-completion event on repeated `go` tokens produced:

| Occurrences | Updates | Final score | State |
| --- | --- | --- | --- |
| 1 | 1 | 0.54 | Learning |
| 2 | 2 | 0.5768 | Learning |
| 4 | 4 | 0.64180352 | Learning |
| 8 | 8 | 0.7433905634312192 | Learning |
| 16 | 16 | 0.8683031941277058 | Known |
| 32 | 32 | 0.9653119026460706 | Known |

These are synthetic policy outcomes, not human learning measurements. Repeated occurrence correlation and empty-translation attribution are separate concerns, not included in the first input-safety acceptance gate.

## First corrective scope

Reject NaN and infinities before any learner mutation; keep finite out-of-range clamping for compatibility. Reject null knowledge units. Reject invalid event enum values in `Record` before changing pending evidence. Add regression tests including a valid pending event followed by an invalid overwrite and an invalid later event.

The focused CI now requires input safety to pass. Verification of this corrective commit must be read from its own CI, not inferred from this document.
Storage exceptions during `Finish` remain an independent failure mode to investigate; early enum rejection alone does not make a multi-update session transactional.

## Interpretation boundaries

No Quest execution, camera input, Unity runtime execution, human learning outcome, or latency was measured here. Source-identity trial success means the characterization checks executed, not that its candidate is safe for product use.
