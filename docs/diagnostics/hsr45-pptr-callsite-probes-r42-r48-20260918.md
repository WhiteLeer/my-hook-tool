# HSR 4.5 PPtr Call-Site Probe Isolation (R42-R49)

## Result

MuMu did not hang during these runs. Its host processes remained responsive and ADB reported the Android VM boot-complete. Some HSR guest processes exited or crashed; this is not evidence that MuMu itself needs restarting.

The caller-side PPtr call site is statically verified in the HSR 4.5 ARM64 image:

- 0x23abcfc: str wzr, [sp, #0x20] (0xb90023ff), immediately before the PPtr call.
- 0x23abd00: bl 0x23a94bc (0x97fff5ef).
- 0x23abd04: ldr w25, [sp, #0x20] (0xb94023f9), the return continuation.

The prior experiment-mode selector was only set in the Windows process and was not forwarded through guest-typetree-ripper.conf into the Android guest. Therefore R44/R45 did not isolate entry-only versus return-only; both installed the both mode. This propagation defect is fixed and the guest config now records hsr45_pptr_scope_mode.

## Evidence

### Confirmed

- R43, baseline Ripper injection with no PPtr/resolver/opaque observers, completed without an Android crash log. HSR PID 13204 remained live during the observation window. Session: artifacts/HookSessions/hsr45-ripper-baseline-r43-20260918/HSR-4.5-MUMU_20260917_161114103.
- R46 guest config explicitly says hsr45_pptr_scope_mode=entry-only. It logged 128 unity.serialized-pptr-callsite-entry-probe rows at 0x23abcfc; no Android crash log was produced. Session: artifacts/HookSessions/hsr45-pptr-entry-only-r46-20260918/HSR-4.5-MUMU_20260917_162106698/runtime/events.ndjson, probe event sequences 101 onward.
- R47 guest config explicitly says hsr45_pptr_scope_mode=return-only. It logged 128 unity.serialized-pptr-callsite-return-probe rows at 0x23abd04; no Android crash log was produced. Session: artifacts/HookSessions/hsr45-pptr-return-only-r47-20260918/HSR-4.5-MUMU_20260917_162326473/runtime/events.ndjson.
- R48 guest config says both. The run had startup-only coverage and no PPtr callsite invocation rows. After the runner completed, the emulator logcat was re-read and the missing crash evidence was recovered: HSR PID 18864 crashed with SIGSEGV at 0xdead1007. Session: artifacts/HookSessions/hsr45-pptr-pair-r48-20260918/HSR-4.5-MUMU_20260917_162509566; captured log: runtime/typetree/android-errors-logcat-captured.txt.

### Observed

- R42 installed the caller-side pair plus resolver and opaque probes, emitted no PPtr entry/return events, then HSR crashed with SIGSEGV at 0xdead1007. Log: artifacts/HookSessions/native-typetree-hsr45/HSR-4.5-MUMU_20260917_160740323/runtime/typetree/android-errors-logcat.txt:153,177-179.
- R42 PPtr-only also crashed before any scope event. Log: artifacts/HookSessions/hsr45-pptr-only-r42-20260918/HSR-4.5-MUMU_20260917_160937298/runtime/typetree/android-errors-logcat.txt:127,151-154.
- R44's supposed entry-only run actually received scopeMode=both and crashed at 0xdead1007. Log: artifacts/HookSessions/hsr45-pptr-entry-only-r44-20260918/HSR-4.5-MUMU_20260917_161537057/runtime/typetree/android-errors-logcat.txt:127,151-155.
- R45's supposed return-only run also received scopeMode=both, but did not produce a crash log. Neither run is valid evidence for the individual probe modes.
- R48 crash backtrace places the first libunity frame at RVA 0x23ababc, before the instrumented 0x23abcfc callsite. R48 emitted no entry/return probe rows, so the crash precedes the observed callsite callback.
- After R48, Android returned to the launcher; ADB still reported boot-complete and MuMu host processes were responsive. Do not describe this as a MuMu hang.

## Interpretation

- Confirmed: each caller-side single probe can install and emit records independently when the guest mode is actually configured.
- Confirmed as a crash-correlated configuration: R48's both-mode run crashed before reaching the instrumented PPtr callsite. R42/R44 also crashed with the combined instrumentation path. The precise low-level cause is not isolated, but the pair must not be used for further runtime collection.
- Individual entry-only and return-only modes each reached their respective probe safely in R46/R47. Do not combine them on this call path.
- No PPtr argument is yet labeled PathID, FileID, InstanceID, or object identity. Entry and return records from separate runs cannot form a same-invocation bridge.
- No field-level TypeTree, Material-to-Shader binding, AS sourceFile#PathID mapping, or RenderDoc EID bridge was obtained.

## R49 Result

- The .NET 8 tool and both Android native architectures built successfully; profile-check and the opaque-callsite audit tests passed.
- MuMu remained responsive and boot-complete. The HSR 4.5.0 process ran under the single return-only probe; no R49 SIGSEGV was found in the current logcat window. The foreground remained `ComboSDKActivity`, and guest login telemetry reported `logged_in=false`, `uid=0`; this was not an in-game scene run.
- R49 session `artifacts/HookSessions/hsr45-pptr-return-resolver-r49-20260918/HSR-4.5-MUMU_20260917_164304669/runtime/events.ndjson` contains 128 return-site rows and 972 ARM64 resolver-candidate rows. All 972 resolver rows have the exact configured caller `0x23abd18` and satisfy `outputValue32 == resolver argument1_32`; they share the same capture session/process and the recorded thread is the PPtr-to-resolver thread.
- The resolver return was nonzero in 820 rows, but no returned value had an observed vtable type; Material/Shader typed returns: 0. The PPtr return event captured raw words, but serialized-field Transfer context and Transfer-object context were absent in all 128 rows.
- Therefore R49 confirms only a same-thread static callsite/output-slot-to-resolver-argument dataflow for opaque values. It does not identify those words as FileID/PathID, prove a Unity object identity, or create a Material/Shader binding.

## Current Decision

Return-only is a viable and stable observation mode for this HSR 4.5 caller pair, and the output slot can be bridged to the immediately following resolver argument without combining entry and return instrumentation. The end goal remains feasible in principle but is not yet demonstrated: the present resolver call returns no runtime-typed Material/Shader object and the call occurs without field/Transfer owner context. Next work must trace the meaning/consumer of the resolver's opaque return through static ARM64 dataflow, then obtain a same-object field owner and typed object in an actual in-game scene. Do not broaden observer combinations or relabel opaque values to compensate for the missing evidence.

## Next Gate

After HSR is authenticated and reaches an in-game scene, repeat the bounded return-only experiment with runtime Transfer type observation enabled for ClassIDs 21 and 48. In parallel, statically trace the resolver's returned object through the `0x23abd18` caller path. Accept a Material/Shader edge only if the object's type is independently observed in-session and its owning serialized field is connected through the same object/call chain. Then separately establish sourceFile#PathID semantics and RenderDoc EID linkage.

## Static Resolver-Return Consumer Trace after R49

### Confirmed

- The inspected HSR 4.5 ARM64 ELF is `artifacts/analysis/HSR-4.5/lib/arm64-v8a/libunity.so`, SHA-256 `3fd4436ae093c58a958ed78c144f198342b51ade083e7dae9f81d905fb2be68f`. Its `.text` section maps file offset `0x8ec600` to virtual address `0x8f0600`; the disassembly used this ELF section mapping.
- The caller sequence is `0x23abd00 BL 0x23a94bc`, `0x23abd04 LDR W25,[SP,#0x20]`, `0x23abd08 MOV X0,X19`, `0x23abd0c MOV W2,W24`, `0x23abd10 MOV W1,W25`, `0x23abd14 BL 0x23a9a7c`. This confirms the PPtr output slot is loaded and forwarded as resolver argument 1 in this caller path; it does not assign semantics to the value.
- At resolver continuation `0x23abd18`, `X0` is null-checked. On the non-null path, the caller reads `[X0,#0x0c]`, stores the returned pointer in `X24`, and later reads `[X24,#0x68]` before dereferencing a subsequent `+0x08` field. This is static consumer/dataflow evidence only; neither the return nor nested field is identified as a Unity object, Material, Shader, PathID, or InstanceID.
- The inspected instruction windows were decoded as ARM64 from the SHA-256-matched ELF. Runtime capture has not been repeated after the MuMu restart in this turn.

### Decision / Next Gate

- R49 remains an opaque same-call value handoff. Static use of `X0`/`X24` narrows an observation seam but does not establish identity or type.
- Do not infer that the resolver return's `+0x68` field is an asset pointer. First use a single return-only observer in an authenticated HSR 4.5 scene to record, within the exact `0x23abd18` invocation, the resolver result and only memory reads proven safe by the existing bounded-read helper. Compare candidate fields to independently observed runtime type evidence in that same process/call chain.
- No source or native code was changed for this static audit. Current runtime state after restart is at HSR's SDK/login Activity; no new runtime events were collected.

## R50 Runtime Result

### Confirmed

- MuMu VM 0 was restarted before R50. After capture, host processes `MuMuNxMain` (PID 35744) and `MuMuNxDevice` (PID 39836) were responsive and ADB `127.0.0.1:16384` was online. HSR remained running; no second restart was needed.
- R50 used the single return-only PPtr observer and bounded resolver candidate observer in an authenticated HSR 4.5 overworld. Session: `artifacts/HookSessions/hsr45-world-pptr-return-r50-20260918/HSR-4.5-MUMU_20260917_210408160`.
- `runtime/events.ndjson` contains 1,663 events. Exactly 28 `unity.serialized-object-resolve-candidate` events report `returnTypeObserved=true` and `targetObjectType=true`; all 28 originate at caller `0x4000233a9218` and independently report `returnVtableObservedType.persistentTypeId=21`, `className=Material`, `objectSize=264`.
- In those same 28 events, `pptrCorrelationAudit` reports `configuredPPtrOrigin=false`, `managerMatches=false`, `activePPtrContextMatches=false`, `resolverCallerMatches=false`, `outputReadable=false`, and `outputEqualsResolverArgument=false`. Thus the typed Material resolver returns are not joined to the observed PPtr output slot or its call context.
- Six `unity.transfer` rows report `typeId=21`, `typeName=Material`; three ARM64 rows and their x86_64 bridge copies share the same object pointer values. Architecture copies are not counted as six independent Material objects.
- The run did not produce a field-level TypeTree: `release.ttbin` is 31 bytes, and the runtime log explicitly says native TypeTree transfer was skipped/disabled. The RTTI catalog is not a field TypeTree. Finalization reports AS Shader=0, Material=0, explicit associations=0.

### Interpretation

- Confirmed: the `0x23a9218` resolver candidate path can return runtime-typed Material objects in the live HSR 4.5 scene without combining PPtr entry/return observers.
- Observed but not linked: these resolver returns and Material Transfer observations exist in the same process/session, but the event schema provides no same-invocation owner/PPtr relation. Pointer coincidences, thread membership, call ordering, vtable identity alone, or architecture-copy equality must not be promoted to an asset identity edge.
- Blocked: no actual serialized field TypeTree, no Material `m_Shader` field context, no sourceFile#PathID mapping, no exact Material-to-Shader identity edge, and no RenderDoc EID bridge.
- R50 was stable and did not crash MuMu/HSR. Native TypeTree collection remains a separate unproven path; do not enable it by merely reusing this successful observer profile.

### Next Gate

Trace the exact caller and owner path that produces these typed Material resolver returns and establish a same-invocation relation to Transfer field serialization. The next instrumented experiment must emit a direct invocation/correlation token and independently typed owner/target within that call chain; otherwise record the relation as blocked. Keep the current HSR session running unless it actually becomes unresponsive.
