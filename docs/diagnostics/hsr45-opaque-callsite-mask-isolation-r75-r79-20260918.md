# HSR 4.5 opaque resolver callsite isolation: R75-R79

## Result

The current opaque-resolver-to-typed-object join is not established. R79 installed the ARM64 guest probes and collected raw resolver-call observations, but no typed resolver candidate returned to the expected continuation at `libunity.so + 0x23a9218`. Do not associate records by nearby timestamps, similar argument values, object names, or addresses.

MuMu itself was healthy after R79: `MuMuNxMain` was responding, Android reported `sys.boot_completed=1`, and the HSR process remained present. No emulator restart was warranted. R75 had crashed the HSR app, not the emulator.

## Evidence classification

### Confirmed

- R75 used opaque observer mask 15. The guest hit a SIGSEGV at `0xdead1007` in the libhoudini path; the backtrace included `libunity.so + 0x23a9500`, before the first inner probe at `+0x23a95a8`. The emulator remained booted. R75 is a probe-combination crash and is not evidence that MuMu is frozen.
- R76 (mask 2) and R77 (mask 4) completed without crashing but produced no callback at their respective opaque sites.
- R78 (mask 8) completed and recorded 256 raw resolver-argument callbacks, but none had an active parent scope and no direct typed return was established.
- R79 rebuilt and ran with the stricter candidate-return gate. The actual ARM64 guest installation record reports the opaque observer `status=installed`, `probeMask=8`, and `resolverArgumentProbeStatus=0` at event sequence 101. The host-side x86_64 attempt correctly reports unsupported target architecture and is not the active guest installation.
- R79 contains 256 `unity.hsr45.opaque-resolver-arguments-raw` records and 1,017 `unity.serialized-object-resolve-candidate` records. All 256 raw records have `parentScopeMatched=false`. No typed candidate has caller continuation `0x4000233a9218` (the session module base plus RVA `0x23a9218`), and none carries `sameRawResolverCallsiteInvocation`.
- An example R79 raw observation is event sequence 123: `instructionRva=0x23a9210`, `nextCallRva=0x23a9214`, `parentScopeMatched=false`, thread 46955. The typed candidate at event sequence 122 is a different PPtr resolver path (`caller=0x4000233abd18`) and has no Material/Shader return. Its proximity and matching low-width arguments do not constitute a join.
- The initial audit output reported `verifiedCallsiteToTypedReturnLinks=0` and `endToEndIdentityBridge=not_proven`, but its aggregate install status conflated entry-only PPtr scope with the resolver-site probe. The audit now reports `opaqueResolverProbeInstallStatus=installed`, `safePPtrScopeInstallStatus=not_installed_or_not_observed`, and overall `installStatus=installed` for R79. This corrects reporting only; it does not change the zero-link result.

### Observed

- R79 rebuilt successfully with the updated TypeTreeRipper runtime. x86_64 and ARM64 outputs were produced; the guest library SHA-256 is recorded in the build output and runtime manifest.
- Candidate caller distribution is 989 events from `0x4000233abd18` and 28 from a host-translated route; zero use the expected `0x4000233a9218` return continuation.
- The raw probe is at RVA `0x23a9210`; static disassembly has a direct call at `0x23a9214` returning to `0x23a9218`. Dynamic evidence does not show the typed observer receiving that call's return.

### Hypothesis

- The target address at RVA `0x23a9214` may not be the function entry intercepted by the current typed resolver observer, or the observed instruction may execute through a guest translation path that bypasses the assumed hook boundary. This is not confirmed.
- The audit's aggregate install-status logic should distinguish host-side unsupported attempts from the guest ARM64 install before it is used as a gate. This affects reporting only; it does not create missing runtime evidence.

### Blocked

- No same-invocation raw-call to typed-return link.
- No live Material `m_Shader` value or Material-to-Shader edge.
- No `sourceFile#PathID` association and no RenderDoc EID bridge.
- R75's combined mask is unsafe for further repetition; do not return to mask 15 without a site-specific safety rationale.

## Evidence files

- R75 events: `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-r75-opaque-link-20260918/HSR-4.5-MUMU_20260918_000419879/runtime/events.ndjson`; locate SIGSEGV details in the matching MuMu tombstone `tombstone_08` and host experiment output.
- R76 session: `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-r76-probe-return-only-20260918/HSR-4.5-MUMU_20260918_001206631/runtime/events.ndjson`.
- R77 session: `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-r77-probe-consumer-only-20260918/HSR-4.5-MUMU_20260918_001701820/runtime/events.ndjson`.
- R78 session: `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-r78-probe-resolver-only-20260918/HSR-4.5-MUMU_20260918_001930102/runtime/events.ndjson`.
- R79 session: `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-r79-typed-callsite-return-20260918/HSR-4.5-MUMU_20260918_003008404/runtime/events.ndjson`.
- R79 audit: `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/analysis/HSR-4.5/r79-callsite-audit.json`.
- Runtime implementation: `D:/Unpack_Workspace/References/TypeTreeRipper/source/android/android_impl.cpp`.
- Audit implementation and regression test: `D:/Unpack_Workspace/Tools/my-hook-tool/tools/Audit-HSR45OpaqueCallsiteEvidence.ps1`, `D:/Unpack_Workspace/Tools/my-hook-tool/tools/Test-HSR45OpaqueCallsiteEvidence.ps1`.

## Next gate

Before another MuMu run, correct the audit's host-versus-guest install aggregation and add a fixture for R79's actual event shape. Then inspect the exact ARM64 control/data flow around the raw call and determine whether the typed observer's target is the called function entry. The next runtime test should install only the proven target observer and accept a join only when the same thread/call invocation and exact arguments are carried through the call-return boundary. If that cannot be established statically, capture the actual callee entry/return as a separate, narrowly scoped experiment. Do not claim identity progress until a typed Material/Shader result is joined to the exact call.
