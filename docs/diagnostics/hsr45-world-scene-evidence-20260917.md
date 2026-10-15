# HSR 4.5 World-Scene Evidence, 2026-09-17

## Scope

Two narrow MuMu experiments were run against HSR 4.5.0 / Unity 2019.4.34f1. The first used the stable load-boundary, object-consumer, resolve-candidate and Material context observers. The second added only the serialized-field observer with the requested names `m_Shader`, `m_FileID`, and `m_PathID`.

## Confirmed

- The first run entered a real administrative-district gameplay scene while the observer remained active.
- The first run emitted 190 `unity.transfer` records and 1024 `unity.transfer-context-call` records for `Material`.
- The first run emitted 4096 bounded serialized-object load-boundary records, 1024 consumer records, and 65536 PPtr candidates.
- The second run also entered the same gameplay scene and completed without a guest crash.
- The second run installed the serialized-field observer successfully.
- Runtime TypeTree artifacts were still produced and the existing Material/Shader baseline remained available.

## Observed

- In the first run, no `unity.serialized-field-transfer` record was emitted, and no Shader/Material identity or RenderDoc EID record was emitted.
- In the first run, the set intersection between `Material` context `object` values and consumer `objectCandidate` values was zero.
- In the second run, the requested field observer produced zero field-transfer records, including zero `m_Shader`, `m_FileID`, and `m_PathID` records.
- The second run's session was finalized from 76445 runtime events; the hook manifest records `sceneObserved=false` because the automated collector does not infer scene state from screenshots.

## Hypothesis

- The selected serialized-field target is not the field-reader entry used by the Material path exercised in the world scene, or the field access is performed through a different ABI boundary than the current observer assumes.
- The load-boundary and consumer hooks observe real serialized activity, but they are not sufficient to establish Material ownership or Shader reference identity.

## Blocked

- No runtime value for `Material.m_Shader`, `m_FileID`, or `m_PathID` is accepted.
- No same-object relation between Material Transfer, Shader PPtr resolution, sourceFile#PathID, and RenderDoc EID is accepted.
- No address, event order, opaque key, low-bit match, or ResourceId is promoted to an identity.

## Next gate

Perform static call-chain analysis from the observed Material Transfer callsites and the verified serialized field-reader anchors. Add one bounded read-only owner/object-token observation only after a version-checked caller is identified. Accept the bridge only if one token is present in the Material Transfer, the field read, and the later RenderDoc correlation.

## Evidence

- `artifacts/HookSessions/hsr45-long-live-20260917/HSR-4.5-MUMU_20260916_165413806/runtime/events.ndjson`
- `artifacts/HookSessions/hsr45-long-live-20260917/HSR-4.5-MUMU_20260916_165413806/HSR-4.5-MUMU_20260916_165413806.hook`
- `artifacts/HookSessions/hsr45-field-world-20260917/HSR-4.5-MUMU_20260916_170241410/runtime/events.ndjson`
- `artifacts/HookSessions/hsr45-field-world-20260917/HSR-4.5-MUMU_20260916_170241410/HSR-4.5-MUMU_20260916_170241410.hook`
