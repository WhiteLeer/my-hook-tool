# HSR 4.5 R25-R28 Runtime Results

## Scope

Follow-up to the corrected install-site guards. This records runtime startup,
event serialization, and crash evidence. It does not claim a TypeTree or asset
identity bridge.

## Confirmed

- R25 reached HSR 4.5.0 and loaded the ARM64 runtime bridge. Its original
  `runtime/events.ndjson` has 252 lines; line 190 is invalid JSON because the
  `unity.hsr45.opaque-callsite-observer-installed` writer emitted an extra
  quote after `modulePath`. Original SHA-256 before and after analysis is
  `8A27A51376028399AC4393F43CC80596EB81C85FCC6930663A4D25B3FF243AC2`.
- A copy-only repair of line 190 parses all 252 rows. The original log was not
  modified. R25 eventSequence 105 reports the four instruction-window guards
  true and all four `DobbyInstrument` statuses equal to zero; eventSequences
  106, 108, 111, and 113 are load-boundary observations. No opaque producer,
  consumer, or resolver-probe invocation event was recorded in R25.
- The malformed JSON writer was fixed in
  `D:/Unpack_Workspace/References/TypeTreeRipper/source/android/android_impl.cpp`
  by removing the duplicate quote after `JsonEscape(platform.GetModulePath())`.
  Android x86_64, Android ARM64, and the .NET 8 Release CLI all rebuilt
  successfully with zero warnings and errors.
- R26's probe stream shows ARM64 PPtr, resolver-candidate, load-boundary,
  consumer, and opaque callsite observers installed. The opaque install record
  is probe line 207; 32 load-boundary and 12 consumer observations precede the
  process crash. Probe line 233 records one resolver-argument probe event with
  `parentInvocationScope.status=inactive`.
- R26 crashed after about 12 seconds. `llvm-addr2line` maps
  `HookedSerializedPPtr(...)+388` at
  `runtime/typetree/android-errors-logcat.txt:103` to
  `android_impl.cpp:6959`, the indirect call to the original trampoline
  (`blr x25`). The previous stack frame is `libunity.so+0x23a9500`; matching
  Build ID `5a4ba26d6687f32c` and local disassembly map it to the target
  function's `bl ANativeWindow_release@plt` instruction. Thus the observed
  failure is inside the forwarded Unity execution path. It does not establish
  whether the hook altered relevant state or the same call would fail without
  instrumentation.
- R28 was an attempted single-variable isolation with
  `serializedPPtrObserver=false` and `hsr45OpaqueCallsiteObserver=false` in the
  profile. It crashed before the ARM64 observer stage: the captured fault is a
  null dereference at `libunity.so+0x1d0b3e4`, recorded at
  `runtime/typetree/android-errors-logcat.txt:106,118`. Therefore R28 is not a
  valid stability control for R26. The profile was restored to its original
  observer settings after the experiment.
- MuMu VM 0 remains online and ADB-responsive. HSR is currently not running;
  neither failed process required restarting the VM.

## Observed

- R26 eventSequence 148 (probe line 233) records resolver arguments
  `0x0` and `0x3`, but its parent invocation scope is inactive. These values
  remain opaque and are not interpreted as a PathID, file ID, or object ID.
- The R26 `.runtime-events.probe` contains useful partial evidence, but the
  normal `events.ndjson` was not finalized because the process died during
  collection.
- R25's observer installation succeeded, but no producer-to-consumer-to-typed
  resolver invocation was accepted by the strict evidence criterion.

## Hypothesis

- R26's failure is associated with the forwarded PPtr target path, but a
  wrapper/trampoline defect is not established. R28's different early crash
  neither confirms nor refutes that hypothesis.
- The current opaque callsite probe placement may not be inside an active
  parent invocation at resolver time; eventSequence 148 demonstrates this for
  the one observed resolver probe.

## Blocked

- No non-empty field-level runtime TypeTree was validated in R25-R28.
- No runtime `sourceFile#PathID` to typed object, Material-to-Shader object
  relation, or RenderDoc EID identity bridge is proven.
- Do not repeat the same PPtr-enabled launch until the ARM64 ABI, Dobby
  trampoline behavior, and `ANativeWindow_release` caller state are checked
  against the HSR 4.5 binary. The next runtime experiment must include a
  no-Ripper control and log exception-free guest completion.

## Evidence Files

- `artifacts/analysis/HSR-4.5/r25-json-repair-audit-20260917/events-analysis-copy.ndjson`
  — sanitized analysis copy; all 252 rows parse.
- `artifacts/HookSessions/hsr45-trampoline-guard-r25-reuse-20260917/HSR-4.5-MUMU_20260917_125644476/runtime/events.ndjson`
  — original R25 log; line 190 invalid, original hash preserved.
- `artifacts/HookSessions/hsr45-trampoline-guard-r26-reuse-20260917/HSR-4.5-MUMU_20260917_130211834/runtime/.runtime-events.probe`
  — partial R26 stream; observer install at line 207 and resolver probe at line 233.
- `artifacts/HookSessions/hsr45-trampoline-guard-r26-reuse-20260917/HSR-4.5-MUMU_20260917_130211834/runtime/typetree/android-errors-logcat.txt`
  — R26 SIGSEGV stack, especially lines 86-103.
- `artifacts/RelWithDebInfoAndroidARM64/libTypeTreeRipper.so`
  — `llvm-addr2line` maps offset `0x94c68` to `android_impl.cpp:6959`.
- `artifacts/analysis/HSR-4.5/guest/libunity-4.5.0-arm64.so`
  — exact Build ID match; disassembly at RVA `0x23a9500` is the call to
  `ANativeWindow_release@plt`.
- `artifacts/HookSessions/hsr45-pptr-isolation-r28-reuse-20260917/HSR-4.5-MUMU_20260917_130725509/runtime/typetree/android-errors-logcat.txt`
  — R28 early Unity null dereference, especially lines 106-118.

## Next Gate

Static preflight only: validate the ARM64 function prototype and Dobby's
translated-code behavior, inspect arguments/state at the
`ANativeWindow_release` call, and establish a no-Ripper baseline for the same
HSR startup. Only then design another MuMu run. No current evidence justifies
enabling native TypeTree construction or claiming semantic identity.
