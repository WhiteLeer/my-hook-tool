# HSR 4.5 identity bridge scene experiment

## Scope

This is a bounded runtime experiment for the HSR 4.5 MUMU client. It enabled
the existing identity producer, resolve, PPtr, object lookup, resolve-candidate
and Transfer-context observers. It did not enable native TypeTree generation,
raw identity patches, or any new hook target.

## Confirmed

- The HSR 4.5 guest completed the run without a crash.
- The producer observer emitted 49 calls. The producer callsite was recorded as
  guest `libunity.so + 0x18a98d0` in the same event record.
- PPtr observation and resolve-candidate observation established a direct
  same-thread handoff for some records: the resolve candidate retained the
  PPtr caller, result word, key address and raw two-word candidate together.
- The runtime emitted 6 Material Transfer records and 14 Material
  Transfer-context calls in this run.

## Observed

- Resolve calls, where present, exposed opaque manager arguments and an opaque
  return candidate. No field was labelled `sourceFile`, `PathID`, `InstanceID`,
  Material, or Shader by the runtime evidence.
- PPtr records exposed a serialized-PPtr-shaped two-word payload and output
  storage, but no direct object handle for the later Material Transfer.
- Producer calls ran on thread `3011`; the sampled Material Transfer events ran
  on thread `3043`. The event stream contains no same-object or same-call-chain
  relation between those records.
- The run produced no RenderDoc EID and no Material-to-Shader identity link.

## Hypothesis

- The observed producer/PPtr/resolve path is an asset-loading or serialized
  lookup stage that precedes the later Material Transfer stage. This remains a
  hypothesis because the common object or registration record was not emitted.

## Blocked

- It is not valid to treat `argument1_32`, `rawWord8`, `key64`, an address, a
  thread, or event order as `PathID`, `InstanceID`, or an asset key.
- The Material/Shader to `sourceFile#PathID` bridge remains unproven.
- The Material/Shader to RenderDoc EID bridge remains unproven.

## Evidence

- `artifacts/HookSessions/hsr45-identity-bridge-scene-20260916-r1/HSR-4.5-MUMU_20260916_130032020/runtime/events.ndjson`
  - 49 producer calls, 65,536 PPtr candidates, 4,096 resolve candidates,
    4,096 lookup records, 6 Material Transfers, 14 Material context calls.
- `profiles/HSR-4.5-MUMU.json`
  - Version-matched 4.5 observer RVAs and first-instruction guards.

## Decision

The experiment is accepted as runtime coverage and same-thread PPtr-to-resolve
evidence only. It does not close the identity bridge. The next experiment must
instrument a version-checked registration/output boundary that exposes the same
runtime object on both the serialized lookup path and the Material Transfer path.
