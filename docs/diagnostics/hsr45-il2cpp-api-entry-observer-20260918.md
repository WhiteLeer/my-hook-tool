# HSR 4.5 `il2cpp_get_api_table` Entry Probe

## Scope

Two fresh HSR 4.5.0 processes were tested while reusing the MuMu VM. The experiment only observes the exact ARM64 `libil2cpp.so` Build ID `7e6d81db351101e1`, RVA `0x631d898`, and first instruction `0x17ff970f`. It records raw LR, SP, and X0-X7 if the entry executes; it does not change the register context or infer argument meanings.

## Confirmed

- The installed HSR ARM64 `libil2cpp.so` is SHA-256 `839dd86a6fe15336e907a57595d6f3841d460e9378c0f026d5f7ab1bbd834118`, GNU Build ID `7e6d81db351101e1`. The matching static entry is a four-byte global function at RVA `0x631d898`; its first instruction branches to RVA `0x63034d4`.
- In the pre-load run, the observer DSO load is recorded at `events.ndjson:82`; the `libil2cpp.so` NativeBridge load call returns and is recorded at line 84; the ARM64 observer installation appears later at line 95. The installation has matching Build ID and instruction and `dobbyStatus=0`.
- In the 1 ms poll run, the observer DSO load is at line 82, the target NativeBridge load return at line 84, and the ARM64 observer installation at line 114. It again validates the same Build ID/instruction and reports `dobbyStatus=0`.
- Neither run contains an `il2cpp-api-table-entry` callback. Each completed session reports 514 runtime events, a 4684-byte RTTI catalog, AS Shader=0, Material=0, and explicit associations=0.
- Both test runs reused MuMu; the experiment runner restarted only the HSR Android process. ADB remained connected. The MuMu host/VM itself was not restarted.
- Read-only ELF inspection confirms the named dynamic symbol and the entry branch. `llvm-readelf -rW` did not report relocations by this symbol name; that bounded result does not exclude direct branches, indirect calls, or use by the external NativeBridge loader.
- A bounded exact-image scan of executable `PT_LOAD` ranges decoded all 9,032,893 direct AArch64 `B`/`BL` instructions. It found no direct `B`/`BL` caller targeting RVA `0x631d898`; the sole direct branch to RVA `0x63034d4` is the instruction at `0x631d898` itself. This does not cover ADRP/LDR/BLR patterns, function-pointer tables, or callers outside this ELF.
- The matching live `libunity.so` (SHA-256 `3fd4436ae093c58a958ed78c144f198342b51ade083e7dae9f81d905fb2be68f`, Build ID `5a4ba26d6687f32c`) contains the literal `il2cpp_get_api_table` at file offset `0x3779c0`, but has no dynamic symbol or relocation with that name. A streamed scan of 8,577,447 decoded `.text` instructions found no direct ADR or short ADRP+ADD/LDR reference to the literal. Nearby strings include `UnityEngine.dll` and a managed-method parameter-count message; the literal alone is not evidence of a call.

## Interpretation

The pre-load change moved the observer DSO load ahead of the target-library load request, but did not arm the ARM64 entry before the NativeBridge load call returned. Reducing the ARM64 poll interval from 250 ms to 1 ms reduced the post-return delay, but installation still followed the return event. This leaves the library-initialization interval unobserved. Bounded direct-call and Unity-code-reference scans found no direct in-library caller/reference to this exported entry. Zero callbacks therefore do **not** establish that the API entry was never called; an external/indirect call may still occur, the implementation may have called it during loading, or the entry may not be used on this runtime path. The current tests cannot distinguish those explanations.

The x86_64 `unsupported_host_architecture` installation event is expected for the host copy and is not evidence about the ARM64 observer. Only the ARM64 `installed` event is relevant.

## Decision / Next Gate

Do not add parameter labels, TypeTree decoding, or identity claims from these runs. Static scans for direct internal references are complete but bounded; the API-table route has no affirmative caller evidence. The next useful step is to establish a same-process NativeBridge symbol-resolution/call event or an exact-build indirect caller in another module. Without that evidence, stop investing in this entry and move to a separately proven runtime TypeTree serialization/consumer boundary.

## Evidence

- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-il2cpp-api-entry-preload-r3-20260918/HSR-4.5-MUMU_20260918_064111376/runtime/events.ndjson`: lines 82, 84, 95; no `il2cpp-api-table-entry` event; 514 records.
- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-il2cpp-api-entry-fast-poll-r4-20260918/HSR-4.5-MUMU_20260918_065140057/runtime/events.ndjson`: lines 82, 84, 114; no `il2cpp-api-table-entry` event; 514 records.
- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/analysis/HSR-4.5/il2cpp-static-20260918/libil2cpp-live-20260918.so`: pulled from the live package path; SHA-256 above; exact Build ID verified. The 447,497,944-byte file is retained locally for reproducible static inspection and is not a distributable artifact.
- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/analysis/HSR-4.5/il2cpp-static-20260918/libunity-live-20260918.so`: exact live Build ID and SHA-256 above; bounded `.text` scan found no direct ADR/ADRP reference to the symbol-name literal.
- `D:/Unpack_Workspace/References/TypeTreeRipper/source/android/android_impl.cpp`: opt-in observer and pre-load/fast-poll experiment implementation.
- `D:/Unpack_Workspace/Tools/my-hook-tool/tools/Build-HSR45Runtime.ps1`: reproducible build/deploy entry point; both native ABIs and .NET 8 build succeeded with zero warnings/errors in each build.
