# HSR 4.5 Load-Return to Consumer Direct-Chain Audit, 2026-09-17

## Scope

Read-only audit of session `HSR-4.5-MUMU_20260917_035126104`. The question is
whether the existing load-boundary and downstream-consumer observations contain
a direct runtime relation, and whether that relation reaches a Material
Transfer or a serialized asset identity.

## Confirmed

- The session contains 4,096 `unity.serialized-object-load-boundary` records and
  1,024 `unity.serialized-object-consumer` records with a nested
  `loadBoundaryCallChain`.
- All 1,024 linked consumer records have `directCallerMatch=true`, a readable
  vtable at `objectCandidate`, and exact equality between:
  - the consumer `keyRecord` argument and the load's recorded output-slot token;
  - the consumer `objectCandidate` argument and the load's recorded return
    object candidate.
- The ARM64 observer compares those values on a thread-local context and emits
  the nested chain only when both comparisons match. The consumer's return PC
  also matches the configured direct caller `0x23ac580`.
- Static comparison against the `unity.transfer` observer's Material vtable
  found one unique Material vtable value in this session; none of the 1,024
  consumer candidate vtables equals it.
- The version-matched static caller sequence at `0x23ac534` calls the load
  boundary at `0x23ab91c`, retains its `x0` return in `x25`, and forwards that
  value in `x6` to the consumer at `0x23b9774` (`BL` at `0x23ac57c`; return PC
  `0x23ac580`). This is direct static register/data-flow evidence for the
  observed load-return-to-consumer candidate chain.
- This proves a bounded relation between a load return candidate and a
  downstream consumer argument in this call path. It does not prove the
  candidate's Unity class or serialized-asset identity semantics.

## Observed

- There are 53 distinct ARM64 object pointers in this session's `unity.transfer`
  records; all observed Transfer records are typeId 21 (Material). Exact
  object-pointer comparison found zero intersections with the 1,024 linked
  consumer candidates, and exact vtable comparison also found zero matches.
- The 1,523 nonzero `returnCandidate` values among the
  `unity.serialized-object-resolve-candidate` records also have zero exact
  pointer intersections with those Material Transfer objects.
- `keyOpaque64` remains an opaque field. The PPtr observer records raw words at
  offsets `x2+0` and `x2+8`, but this session does not independently establish
  those values as `m_FileID`/`m_PathID` for a particular Material or Shader.
- The finalized `.hook` still reports `nonEmptyTypeTree=false`; this session
  did not capture field-level TypeTree data.

## Hypothesis

- The loaded candidates may include Unity objects of classes not represented in
  the observed Transfer set, or the consumer may process a non-Unity wrapper.
  The available records do not distinguish these cases.

## Blocked

- No load candidate is proven to be a Material or Shader object.
- No candidate is joined to a typed `sourceFile#PathID` pair.
- No Material/Shader object from this chain is joined to a RenderDoc EID.

## Next gate

Use an independently verified runtime class/type source for the exact returned
candidate, then establish a direct same-object Material Transfer relation. In
parallel, locate a version-matched object-load boundary whose arguments
semantically expose the serialized file and path identifier. Do not reinterpret
`keyOpaque64`, raw PPtr words, candidate pointers, or RenderDoc ResourceIds as
those identifiers. Only after both gates pass should a same-frame RenderDoc EID
join be attempted.

## Evidence

- `artifacts/HookSessions/hsr45-in-game-mshader-r2-abi-20260917/HSR-4.5-MUMU_20260917_035126104/runtime/events.ndjson`
  - 4,096 load boundaries; 1,024 linked consumer events; 1,024/1,024 exact
    output-slot and return-candidate comparisons; 1,024/1,024 direct caller
    matches; zero consumer-to-Material-Transfer pointer intersections; zero
    nonzero resolve-candidate-to-Material-Transfer intersections.
- `source-analysis-notes/hsr45-downstream-consumer-20260916.md`
  - static caller chain and register flow from the load return to consumer x6.
- `References/TypeTreeRipper/source/android/android_impl.cpp`
  - `HookedSerializedObjectLoadBoundary` and
    `HookedSerializedObjectConsumer` implement the exact same-thread token and
    return-candidate comparisons.
- `artifacts/HookSessions/hsr45-in-game-mshader-r2-abi-20260917/HSR-4.5-MUMU_20260917_035126104/HSR-4.5-MUMU_20260917_035126104.hook`
  - completed session; no non-empty TypeTree and no accepted identity links.
