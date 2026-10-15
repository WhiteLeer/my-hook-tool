# HSR 4.5 NativeBridge/TPRT Crash Isolation

## Scope

This note records a restarted MuMu VM, early-load crashes, matched observer
controls, and delayed-load follow-up runs. It does not establish TPRT dispatch
semantics, field-level TypeTree, serialized identity, Material-to-Shader object
identity, or a RenderDoc EID bridge.

## Confirmed

- MuMu VM 0 was restarted and ADB `127.0.0.1:16384` returned online. The HSR
  process was absent immediately after restart.
- In r3, the exact ARM64 TPRT branch target was on `libunity.so` Build ID
  `5a4ba26d6687f32c`, RVA `0x2e7e5d8`, with bytes `0xd61f0060` but mapping
  permissions `r--p`. The observer reported Dobby status 0, had no callback
  among 177 events, and HSR crashed at `libunity.so+0x1d0b3e4`, fault `0xfc`.
- R5's executable-range guard rejected the same non-executable mapping. It
  prevented Dobby instrumentation at that target, but the mode-6 NativeBridge
  path and early ARM64 DSO load remained; HSR still crashed at the same Unity
  RVA.
- R4 is not a matched control: it used probe mode 0, an APK-local ARM64 library,
  and no extra DSO. Do not use it to attribute the mode-6 crash.
- R6 matched r5's mode-6/ARM64 DSO path and differed by disabling only the
  TPRT branch observer. HSR still crashed at the same Unity RVA. Thus the TPRT
  branch observer is not required for that crash.
- R7 retained mode-6 x86 NativeBridge callback hooks but used a sentinel trigger
  so the ARM64 companion was not loaded. HSR completed the RTTI collection
  without a recorded crash.
- R8 (Transfer off, vtable on), R9 (both off), and R10 (Transfer on, vtable
  off) all used the early `libmain.so` companion trigger and had
  `context_observer=1`. R8 and R10 crashed at the same Unity RVA; R9 completed.
  This does not isolate all shared observer effects.
- R11 changed the companion trigger to `libil2cpp.so`, with Transfer on and
  vtable probing off. Event 11 records the companion load after that library's
  NativeBridge load call. The session completed with 215 events and a 7755-byte
  RTTI catalog; its output reports AS Shader 0, Material 0, explicit
  associations 0. The captured Ripper logcat contains no fatal signal.
- R12 used the delayed `libil2cpp.so` trigger with Transfer off and vtable
  probing on. It completed with 223 events and 16 `unity.vtable-probe` events;
  the captured Ripper logcat contains no fatal signal. Its `release.ttbin` is
  only 31 bytes, so it is not a field-level TypeTree result.
- The TypeTreeRipper x86_64/ARM64 and .NET 8 builds succeeded with zero
  warnings/errors after adding the executable-range guard. Source and deployed
  Android library hashes matched at that build.

## Observed

- In these sessions, the crash correlates with loading the ARM64 companion
  after `libmain.so` when Transfer or vtable observation is enabled. Delayed
  loading after `libil2cpp.so` completed in one run per observer configuration.
- This is evidence of a timing/configuration association, not proof of a
  specific race or causal instruction. The early and delayed runs were not
  repeated enough to establish reliability.
- R11's Transfer observer installations do not prove that a relevant Transfer
  callback executed; no callback event was recorded. Likewise, r12's vtable
  probe records are object/vtable observations, not field decoding.
- Earlier R28 also crashed at the same Unity RVA with PPtr and opaque-callsite
  observers disabled, but it was an injected run, not an uninstrumented
  baseline.

## Hypothesis

- Loading the ARM64 companion only after `libil2cpp.so` may avoid startup-time
  interference caused by loading it after `libmain.so`. Competing explanations
  include timing variation or other process-state differences.

## Blocked

- No TPRT dispatch invocation or ABI meaning was observed.
- No new non-empty runtime TypeTree fields, serialized `sourceFile#PathID`,
  exact Material-to-Shader object edge, or RenderDoc EID association was
  obtained.
- Before changing the default trigger, repeat early/delayed trials with
  identical observer flags and inspect the exact first ARM64 companion work on
  both paths.

## Exact PFN Entry Execution Update (r17, 2026-09-18)

Session hsr45-tprt-pfn-entry-runtime-r17-20260918 supersedes the earlier
statement that no TPRT target invocation was observed. The session used the
exact HSR 4.5.0 libunity.so and libtprt.so Build IDs and installed the opt-in
entry observer on 54 unique g_tprt_pfn_array targets; all installs reported
dobbyStatus=0. Ten target-entry callbacks were recorded at event sequences
104-106, 138-142, and 467-468. The events preserve only raw offsets, addresses,
x0-x8, LR, and SP.

This proves that ten exact table targets were entered in that session. It does
not identify their names, signatures, caller sites, or TypeTree/identity
semantics. Bounded disassembly of the exact-build ELF cannot supply those
semantics from entry shape alone. Live /proc/21741/maps inspection did not
resolve the recorded LR values to executable mappings; because that map output
was not archived, LR is explicitly not accepted as a caller PC.

The .hook still contains no field-level runtime TypeTree, serialized
sourceFile#PathID, Material-to-Shader identity edge, or RenderDoc EID bridge.
The next gate is a reproducible post-login run that establishes one exact
caller/ABI through the NativeBridge translated-code mapping, or an independent
proven Unity field-tree producer/consumer. Do not treat PFN-table execution as
TypeTree evidence.

Evidence:

- artifacts/HookSessions/hsr45-tprt-pfn-entry-runtime-r17-20260918/HSR-4.5-MUMU_20260918_085649064/runtime/events.ndjson — install events, summary event 57, ten call events, and module identities.
- artifacts/HookSessions/hsr45-tprt-pfn-entry-runtime-r17-20260918/HSR-4.5-MUMU_20260918_085649064/HSR-4.5-MUMU_20260918_085649064.hook — completed output and remaining empty extraction/association summary.
- artifacts/analysis/HSR-4.5/hsr45-runtime-support-libs-20260918/libtprt.so — same-build ARM64 ELF used for bounded disassembly.

## Evidence Files

- `artifacts/HookSessions/hsr45-tprt-dispatch-after-vm-restart-r3-20260918/HSR-4.5-MUMU_20260918_073454723/runtime/events.ndjson` — events 78-85; 177 records, no dispatch callback.
- `artifacts/HookSessions/hsr45-tprt-dispatch-after-vm-restart-r3-20260918/HSR-4.5-MUMU_20260918_073454723/runtime/typetree/android-errors-logcat.txt` — r3 SIGSEGV, lines 221-236.
- `artifacts/HookSessions/hsr45-tprt-dispatch-exec-guard-r5-20260918/HSR-4.5-MUMU_20260918_075250770/runtime/events.ndjson` — Dobby hook installation, early DSO load, and guard rejection, lines 3-5, 81, 83.
- `artifacts/HookSessions/hsr45-tprt-dispatch-exec-guard-r5-20260918/HSR-4.5-MUMU_20260918_075250770/runtime/typetree/android-errors-logcat.txt` — r5 SIGSEGV, lines 124-139.
- `artifacts/HookSessions/hsr45-arm64-ripper-transfer-no-vtable-control-r10-20260918/HSR-4.5-MUMU_20260918_081057099/runtime/guest-typetree-ripper.conf` — early `libmain.so`, Transfer on, vtable off, context on.
- `artifacts/HookSessions/hsr45-arm64-ripper-transfer-no-vtable-control-r10-20260918/HSR-4.5-MUMU_20260918_081057099/runtime/typetree/android-errors-logcat.txt` — r10 SIGSEGV at the same Unity RVA, lines 229-240.
- `artifacts/HookSessions/hsr45-arm64-ripper-catalog-only-control-r9-20260918/HSR-4.5-MUMU_20260918_080955257/runtime/guest-typetree-ripper.conf` — early trigger, Transfer/vtable off, context on; completed control.
- `artifacts/HookSessions/hsr45-arm64-ripper-no-transfer-control-r8-20260918/HSR-4.5-MUMU_20260918_080822433/runtime/guest-typetree-ripper.conf` — early trigger, Transfer off, vtable on, context on.
- `artifacts/HookSessions/hsr45-arm64-ripper-no-transfer-control-r8-20260918/HSR-4.5-MUMU_20260918_080822433/runtime/typetree/android-errors-logcat.txt` — r8 SIGSEGV at the same Unity RVA.
- `artifacts/HookSessions/hsr45-arm64-ripper-delayed-il2cpp-transfer-r11-20260918/HSR-4.5-MUMU_20260918_081649616/runtime/guest-typetree-ripper.conf` — delayed `libil2cpp.so`, Transfer on, vtable off.
- `artifacts/HookSessions/hsr45-arm64-ripper-delayed-il2cpp-transfer-r11-20260918/HSR-4.5-MUMU_20260918_081649616/runtime/events.ndjson` — event 11 confirms delayed companion load; 215 records, including 16 observer installations and no Transfer-call event.
- `artifacts/HookSessions/hsr45-arm64-ripper-delayed-il2cpp-transfer-r11-20260918/HSR-4.5-MUMU_20260918_081649616/HSR-4.5-MUMU_20260918_081649616.hook` — completed output and summary counts.
- `artifacts/HookSessions/hsr45-arm64-ripper-delayed-il2cpp-vtable-r12-20260918/HSR-4.5-MUMU_20260918_082007225/runtime/guest-typetree-ripper.conf` — delayed `libil2cpp.so`, Transfer off, vtable on.
- `artifacts/HookSessions/hsr45-arm64-ripper-delayed-il2cpp-vtable-r12-20260918/HSR-4.5-MUMU_20260918_082007225/runtime/events.ndjson` — event 11 confirms delayed companion load; 223 records and 16 vtable-probe records.
- `artifacts/HookSessions/hsr45-arm64-ripper-delayed-il2cpp-vtable-r12-20260918/HSR-4.5-MUMU_20260918_082007225/runtime/typetree/release.ttbin` — 31-byte output, not a decoded field tree.
- `docs/diagnostics/hsr45-r25-r28-runtime-results-20260917.md` — prior injected same-RVA crash, lines 41-45; not an uninstrumented baseline.

## PFN Repeat and Material/Shader Transfer Update (r18-r19, 2026-09-18)

MuMu remained responsive during this follow-up, so the VM was not restarted.
The controlled r18 run repeated the PFN-only observation after entering the HSR
world. It completed with 563 events, 54 distinct PFN target installations and
10 entry calls. It still yielded only an RTTI catalog; no field-level runtime
TypeTree or AS Material/Shader association was emitted.

The r19 run enabled the existing Transfer observer for Unity ClassIDs 21
(Material) and 48 (Shader), while retaining the PFN observer and delayed
ARM64 companion load. It completed with 860 events: 98 Material and 158 Shader
Transfer events, representing 49 and 79 distinct observed object-pointer
values respectively. It also recorded six Transfer callsite events and four
ABI diagnostic events.

The source contract matters: `unity.transfer-abi-diagnostic` serializes the
result of `WriteTypeTreeVtableEvidence`; the `transfer.type_tree_layout` string
is emitted while reporting raw candidate layout words, not a decoded tree.
`unity.transfer-layout` explicitly uses `status=observed_not_decoded`. The
Material/Shader snapshots therefore remain opaque memory evidence, not decoded
serialized fields.

An exact pointer-byte scan across every captured Material object snapshot and
its transferWords found zero exact occurrences of the 79 captured Shader
object pointers. This only rules out direct pointer bytes in those captured
ranges. It does not rule out indirection, encoded references, or data stored
elsewhere. Same thread, shared transfer batches, event ordering, and address
proximity are not accepted as binding evidence.

**Conclusion:** r18/r19 improve observability but do not close the target
identity chain. There is still no runtime Material-to-Shader edge, serialized
`sourceFile#PathID`, or RenderDoc EID bridge. The next experiment should start
from the already verified Material TypeTree's `m_Shader.m_FileID` and
`m_Shader.m_PathID`, then observe the exact deserialization/resolution field
boundary for one Material and Shader in the same call chain. Do not broaden
object scans or infer a join from the current Transfer batch.

Evidence:

- `artifacts/HookSessions/hsr45-tprt-pfn-entry-world-r18-20260918/runtime/events.ndjson` — 563 events, PFN installation/call records.
- `artifacts/HookSessions/hsr45-tprt-pfn-transfer-material-shader-r19-20260918/runtime/events.ndjson` — r19 Transfer, callsite, diagnostic, and PFN records.
- `artifacts/HookSessions/hsr45-tprt-pfn-transfer-material-shader-r19-20260918/hsr45-tprt-pfn-transfer-material-shader-r19-20260918.hook` — completed output and extraction summary.
- `References/TypeTreeRipper/source/android/android_impl.cpp` — lines 3205-3213, 3528-3540, 3841-3855, 3905-3932, and 4394-4428 define the emitted evidence boundaries.
- `artifacts/HookSessions/native-material-current-20260911/HSR-4.5-MUMU_20260911_082249353/runtime/typetree/Material-full.json` — verified Material fields including `m_Shader.m_FileID` and `m_Shader.m_PathID`.
