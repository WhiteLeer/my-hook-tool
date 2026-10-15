# HSR 4.5 Runtime Code-Window Experiment

## Scope

This experiment only validates the provenance of the configured serialized-field observer targets. It does not change hook semantics and does not accept any Material/Shader identity mapping.

## Confirmed

- The rebuilt HSR 4.5 runtime module was deployed and loaded by the x86_64 MuMu guest process.
- Before Dobby installation, the runtime read `0xd10343ff` at RVA `0x2208798` and `0xd103c3ff` at RVA `0x23c87b8`.
- The pre-install code windows begin with the expected AArch64 prologues, so the installer event is not merely a guessed address string.
- The same session later emitted instruction-mismatch events after the process lifecycle changed; those events are separate observations and are not merged with the earlier installed-process evidence.

## Observed

- Session: `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-code-window-20260917-r2/HSR-4.5-MUMU_20260916_162107149/runtime/events.ndjson`
- Event lines 108-109, process `2359`, event sequences `15-16`: installed observations with `actualFirstInstruction` equal to the configured values and pre-install `codeWindowHex`.
- Event lines 71671-71672, process `2359`, event sequences `33-34`: later mismatch observations with host-looking first words and different code windows.
- The runtime module base reported by the observer is `0x400021200000`; the configured targets are `0x400023408798` and `0x4000235c87b8`.
- The runtime mapping resolves to `/data/app/.../lib/arm64/libunity.so`; its `fileOffset + offsetWithinMapping` is exactly `0x2208798`.
- The runtime-mapped file pulled through ADB has the same size and SHA-256 as the extracted APK library (`46105336`, `3FD4436AE093C58A958ED78C144F198342B51ADE083E7DAE9F81D905FB2BE68D`) and still has `00e4006f1f0002391f4400f9017542fd...` at file offset `0x2208798`.
- The in-process pre-install code window at that mapped target begins `ff4303d1fe4b00f9f85f0aa9f6570ba9...`, so the discrepancy is between the mapped file bytes and the live in-process code view, not between two different APK files.

## Hypothesis

- MuMu's Native Bridge exposes a live code view whose bytes are not equivalent to the ARM64 mapped-file bytes at the same RVA. The observer is reading runtime-rewritten, relocated, or translated code rather than the packaged ELF contents.
- The later mismatch events may come from another module view or a lifecycle after code replacement. This is not yet enough to classify the target as a valid serialized-field function.

## Blocked

- No same-object Material-to-`m_Shader` field evidence was produced.
- No `sourceFile#PathID` to Material/Shader identity bridge was produced.
- No RenderDoc EID correlation was produced.
- The current RVA cannot be promoted to a version-independent function identity until the runtime code view is mapped back to the exact loaded image or a same-call object-token chain is observed.

## Next Gate

Use the pre-install code window as the runtime provenance record, then perform a bounded call-chain experiment that records the same object token at Material Transfer, serialized `m_Shader` read, and the subsequent RenderDoc correlation. Do not derive a new RVA from name, address proximity, ordering, or low-bit matches.
