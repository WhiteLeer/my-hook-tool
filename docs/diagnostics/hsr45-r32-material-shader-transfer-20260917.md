# HSR 4.5 R32 Material/Shader Transfer Isolation

## Scope

R32 added the Transfer observer to the stable R31 PPtr/resolver-candidate
setup. It was limited to ClassIDs 21 (Material) and 48 (Shader), reused MuMu
VM 0, and did not enable native TypeTree calls, field reads, load-boundary,
consumer, context, or opaque-callsite observers. The original production
profile was restored after collection.

Session: `HSR-4.5-MUMU_20260917_134205996`

## Confirmed

- MuMu was responsive before launch: ADB reported VM 0 online, Android boot
  complete, and HSR PID 9842 in the foreground. The experiment reused the VM
  and restarted only the HSR Android process; the new process was PID 12844.
- Runtime Transfer-observer installation records identify ClassID 21 as
  `Material` and ClassID 48 as `Shader`, with distinct observed vtables. These
  are runtime type observations from this session, not inferred from names or
  addresses alone. See `runtime/events.ndjson` lines 190-191 (ARM64) and
  359-360 (x86_64), event sequences 105-106 and 17-18.
- Six `unity.transfer` records were emitted, all ClassID 21 / `Material`, for
  three distinct object handles (duplicated across the x86_64 and ARM64 event
  writers). No Shader Transfer invocation was recorded. See lines 1418-1425
  and 1454-1455, event sequences 27-31 and 1322-1353.
- There were 1,027 resolver-candidate events. The direct PPtr output-slot to
  resolver-argument chain was present in 994; 853 resolver returns were
  non-null. The same-session observed-vtable map classified 32 returns as
  Shader and one as Material. Examples are event sequences 115-119 at lines
  200-204 and sequence 168 at line 253.
- None of the 33 typed resolver returns carried the configured direct PPtr
  chain (`typedAndDirect=0`). Exact pointer comparison between the three
  transferred Material objects and all resolver return objects also produced
  zero overlaps. This is a negative join result, not proof that the objects
  are unrelated.
- `runtime/typetree/release.ttbin` is a valid HSR 4.5 / Unity 2019.4.34 Runtime
  container but decodes to zero trees and zero nodes. The strict report is
  `artifacts/analysis/HSR-4.5/r32-runtime-typetree-verify.json` and reports
  Material and Shader both missing.
- The R32 event file contains 1,540 valid JSON rows and is 2,023,549 bytes.
  The generated `.hook` is 2,734,782 bytes. The HSR app remained alive after
  collection; no MuMu VM restart was needed.

## Observed

- The runtime catalog contains RTTI names for Material and Shader, but this is
  not a field-level TypeTree. Its 7,755-byte size does not change the empty
  `release.ttbin` result.
- The NativeBridge raw probe reports `not-called` because the callback table is
  cross-architecture. ARM64 observer installation and runtime records still
  occurred through the separate guest path.
- R32 produced useful runtime class mapping but no sourceFile#PathID values,
  no serialized Material-to-Shader field edge, and no RenderDoc EID join.

## Hypothesis

- The typed Shader returns likely belong to the resolver call path at RVA
  `0x23a9218`, while the configured PPtr producer/consumer correlation covers
  a different caller path (`0x23abd04 -> 0x23abd18`). Static caller evidence
  supports testing a separately instrumented PPtr producer for the
  `0x23a9218` path, but its input/output semantics remain opaque until directly
  observed in one call chain.

## Blocked

- Do not treat resolver `argument1_32`, PPtr `rawWord0/rawWord8`, a timestamp,
  caller order, or a matching low word as PathID, FileID, or object identity.
- The current events do not establish `Material.m_Shader -> Shader`, nor a
  link from either object to a RenderDoc event ID.
- Runtime Material and Shader field trees are still absent. Static selected
  baselines are not substitutes for HSR 4.5 runtime field evidence.

## Next Gate

Trace the exact ARM64 caller feeding resolver return RVA `0x23a9218` and
determine whether that path contains a PPtr or serialized-field producer at
all. The current evidence shows an opaque key entering an internal hash/cache
lookup, not a demonstrated PPtr output slot. Add no PPtr hook until static
instruction/dataflow evidence proves one; if present, observe only that
version-guarded producer and require same-thread, same-process, same-session
output-slot-to-resolver-argument equality plus an independently typed return.
Keep `0x23abd04 -> 0x23abd18` as a separate control. Do not call any opaque
value a PathID without an independently decoded serialized-file record.

## Evidence

- `artifacts/HookSessions/hsr45-material-shader-transfer-r32-reuse-20260917/HSR-4.5-MUMU_20260917_134205996/runtime/events.ndjson`
  - Lines 190-191, 359-360: observed Transfer vtable/type registrations.
  - Lines 200-204, 253: typed resolver returns with same-session vtable map.
  - Lines 1418-1425, 1454-1455: actual Material Transfer events.
- `artifacts/HookSessions/hsr45-material-shader-transfer-r32-reuse-20260917/HSR-4.5-MUMU_20260917_134205996/runtime/typetree/release.ttbin`
  - 31-byte runtime output; decoded tree count and node count are both zero.
- `artifacts/analysis/HSR-4.5/r32-runtime-typetree-verify.json`
  - Strict Material/Shader field-tree coverage is 0/2.
- `artifacts/analysis/HSR-4.5/caller-window.txt`, lines 135-136; and
  `docs/diagnostics/hsr45-material-shader-inworld-pointer-reuse-audit-20260917.md`,
  lines 694-720: static resolver caller at RVA `0x23a9214`, return at
  `0x23a9218`, and parent function trace.
- `profiles/HSR-4.5-MUMU.json`: restored production observer settings after
  the bounded experiment; `profile-check` passed.
