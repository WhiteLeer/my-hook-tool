# HSR 4.5 PPtr-to-typed-resolver boundary (R52-R54)

## Result

MuMu itself remained responsive and ADB-connected in all three runs. R53's HSR guest crashed; that is distinct from a MuMu VM hang. The SDK/login surface was active, not an authenticated in-game scene.

The PPtr entry probe's caller-return address was zero in R52 because it depended on `Hsr45OpaqueModuleBase`, which is initialized only by the separate opaque-callsite observer. R54 removes that dependency and reads the already-configured PPtr-scope return address. The resulting address is valid, and exact output-slot-to-argument dataflow is observed. This still does not join the typed Shader-return calls to that PPtr scope.

## Confirmed

- R52 built both native targets and the .NET 8 tool; the opaque-callsite audit test passed. It recorded 128 PPtr entries and 27 typed Shader returns, but `callerReturn` was `0x0` in those PPtr rows. Session: `artifacts/HookSessions/hsr45-pptr-entry-inner-shader-r52-20260918/HSR-4.5-MUMU_20260917_172135946/runtime/events.ndjson`.
- R53 added `EnableHsr45OpaqueCallsiteObserver`; the HSR process crashed about 12 seconds after launch with SIGSEGV at `0xdead1007`. The captured backtrace includes `libhoudini.so` and Unity/IL2CPP frames. Session log: `artifacts/HookSessions/hsr45-pptr-entry-inner-shader-r53-20260918/HSR-4.5-MUMU_20260917_172726619/runtime/typetree/android-errors-logcat.txt:127,132,137,199`.
- R54 disabled the extra opaque-callsite observer and used the PPtr-scope return-address atomic directly. It recorded 1,643 valid event rows, 128 PPtr entry rows whose `callerReturn` is `0x4000233abd04`, 972 correlated caller-site rows with matching manager and `outputValue32 == resolver argument1_32`, and 27 independently typed Shader returns. Key records: `runtime/events.ndjson:199` (PPtr entry), `:200` (callsite output/argument equality), and `:613-615` (typed Shader returns).
- The 972 exact output-slot matches returned null at caller `0x4000233abd18`; the 27 typed Shader objects returned at caller `0x4000233a9218` without `pptrToTypedObjectCorrelation`. The session reported no Material/Shader asset counts or explicit object associations. The two populations are not joined by this evidence.
- MuMu host processes were responsive, `adb devices` reported `127.0.0.1:16384 device`, and Android boot was complete during checks. HSR guest restart/relaunch did not require restarting the VM.

## Observed

- PPtr entry and typed Shader events appear on the same guest thread in R54, but event-thread equality is not invocation-scope evidence. The typed Shader candidates do not carry the exact active PPtr scope/output-slot proof.
- R53's crash co-occurred with the additional opaque-callsite observer. This is crash-correlated, not yet proof that this observer alone caused the crash.
- Runtime type evidence remains RTTI/vtable classification; the session TypeTree artifact is still the 7,755-byte catalog, not field-level Material/Shader TypeTree data.

## Hypothesis

- The PPtr caller-site wrapper and the typed Shader resolver may be separate stages or separate calls during the SDK initialization path. Static xrefs alone do not prove they resolve the same serialized reference.
- The opaque-callsite observer may be incompatible with this MuMu/Houdini translated execution path. Do not enable it again until static hook-boundary and instruction-level ABI review explains the R53 crash.

## Blocked

- No accepted PPtr -> typed Shader edge; no exact Material owner plus serialized `m_Shader` field context; no `sourceFile#PathID` identity; no RenderDoc EID bridge.
- No valid in-game Material/Shader field capture: the foreground remained at the SDK/login surface.

## Decision and next gate

Keep MuMu running when ADB and the VM are healthy; restart the VM only on an actual VM-level hang. Keep the opaque-callsite observer disabled. The next experiment should be designed from static control-flow around `0x23abd18` and `0x23a9218`, and should add only a verified return/continuation observation that retains the same invocation token across the typed return. Run it in an authenticated game scene, and reject the result unless the PPtr key/output, resolver object, type, and owner field are joined in one thread-local call chain. Do not call R54 a successful identity bridge.
