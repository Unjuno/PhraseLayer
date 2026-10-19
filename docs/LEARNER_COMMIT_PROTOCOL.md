# Learner commit protocol and fault-injection evidence

## Baseline observation

Experiment commit `6ff974cb36fe60dd23e1572cab84982ebcf66c7e`, Experimental Audit run `34143347683`, job `101809968859`, PR merge `96d69f687821c23640e178ce0e564ae51850060f`.
Ubuntu 24.04.4 x64, SDK 10.0.400, .NET 8.0.30, Release. Synthetic in-memory store, two distinct source words, one completion event. No latency, real filesystem, process crash, concurrent threads, or Quest was measured.

Before-save failure left memory at [0.54, 0.50] and durable test-store state at [0.50, 0.50]. After-save response failure left both at [0.54, 0.50]. In both cases retry yielded [0.5768, 0.54] instead of [0.54, 0.54]. The former implementation wrote twice without faults and three times including one failed attempt.

## Correction

1. Prepare all updates on an isolated snapshot with the same adaptation policy; do not touch the real learner.
2. Freeze the resulting complete snapshot and update summary in the encounter session.
3. On commit, reject an intervening in-memory profile change rather than overwriting it. The current profile must match the prepared before or after snapshot.
4. PersistentLearnerModel validates and stages, saves a complete snapshot, and only then publishes the new in-memory state.
5. A failed commit is retried using the exact prepared after-snapshot. It never recomputes a gain from a possibly changed score. Evidence and the completion flag stay frozen while that commit is pending.
6. Successful repeated Finish returns the original summary without another write.

The same audit now gates correction: one complete write without failure, two write attempts with one injected failure, unchanged in-memory scores after a throwing save, and final scores [0.54, 0.54]. Results must be verified from the correction's own CI.

## Contract limits

This is serialized single-owner retry safety. The state-equivalence check is not an atomic multithreaded compare-and-swap. There is no persistent encounter ID, cross-process deduplication ledger, filesystem fsync guarantee, or crash-recovery proof. A retry must retain the original session/batch. A storage implementation must replace a complete snapshot rather than append a non-idempotent operation. Other writers must not bypass this owner.

A store may commit and then throw; durable state is then uncertain until retry/recovery. The batch permits repeated writes of identical state, not an exactly-once count of storage calls. The immutable snapshot collection prevents callers from changing a prepared payload through an array cast.

Memory and work grow with the stored learner profile because preparation copies a snapshot. Quest memory/latency measurements remain required before making performance claims.
