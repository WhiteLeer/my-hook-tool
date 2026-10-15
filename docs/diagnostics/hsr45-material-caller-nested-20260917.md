# HSR 4.5 Material Caller-Nesting Experiment, 2026-09-17

## Scope

One bounded MuMu run used both the serialized field Transfer observer and the configured caller observer for ARM64 `libunity.so` RVA `0x2208798`. Field names were restricted to `m_Shader,m_FileID,m_PathID`. The run reached the character Skills UI and finalized normally.

## Confirmed

- Session `HSR-4.5-MUMU_20260916_174017983` completed with 76,777 runtime events and no reported guest crash.
- The field Transfer observer at RVA `0x23c77a4` installed with the expected first instruction `0xf81a0ffe` for process 2349.
- The caller observer target was RVA `0x2208798`, with expected first instruction `0xd10343ff`.
- The runtime emitted 336 `unity.serialized-field-transfer` records: 168 `m_FileID`, 168 `m_PathID`, and zero `m_Shader`.

## Observed

- The caller observer produced zero `unity.serialized-field-caller` execution records.
- Caller-observer installation records for process 2349 include `installed`, `instruction_mismatch`, and `unsupported-cross-architecture`; installation was therefore not stable across the observed runtime lifecycle.
- No field-transfer record contained `activeSerializedFieldCaller`; no nested caller-to-Transfer context was established.
- The Transfer records preserve opaque stream, argument, output-slot, flags, caller and output-snapshot data. None of those values is decoded here as an object identity or field value.
- The run emitted 106 `unity.transfer` and 1,024 `unity.transfer-context-call` records overall, but this is not a proven link to the filtered field records.

## Static callsite validation

- The observer source records `caller` using `__builtin_return_address(0)` in `HookedSerializedFieldTransfer` (`References/TypeTreeRipper/source/android/android_impl.cpp`, lines 7517-7519). It therefore records the return PC immediately after the original ARM64 callsite, not an inferred function name.
- The run's frequent `m_FileID` return PC `0x40002359f4c0`, normalized against the observed `libunity.so` base `0x400021200000`, is RVA `0x239f4c0`. In the matching HSR 4.5 ARM64 ELF, RVA `0x239f4bc` is `BL 0x23c77a4`, followed by the return PC at `0x239f4c0`.
- The frequent `m_PathID` return PC `0x40002359f58c` normalizes to RVA `0x239f58c`. The preceding instruction at RVA `0x239f588` is also `BL 0x23c77a4`.
- These observations confirm that the field Transfer observer saw real calls from those two ARM64 sites. The helper around RVA `0x239f3f4` appears to process the pair of PPtr fields, but this alone does not establish the owning serialized object or that it is a Material.
- The selected caller hook at RVA `0x2208798` is a separate statically identified `m_Shader` path; its zero runtime execution records mean it was not demonstrated by this session.
- Static disassembly of that path shows calls to the same Transfer target at `0x22087fc`, `0x2208830`, and `0x22088f8`; the first call prepares the static `m_Shader` field-name address, followed by `m_FileID` and `m_PathID` fields in that function. Its direct caller sites include `0x181f7dc` and `0x2208584`. This is strong static field-layout evidence, but no runtime event in this session came from those return PCs.
- Source inspection found that the old event `semantics` value `verified-void-material-field-transfer` was emitted unconditionally for every allowed field name. Historical events carrying that string do not establish Material ownership.
- Field records in this session had no `activeTransferCallChain` and no `activeSerializedObjectLoadBoundary`; thus the records cannot be joined to the top-level object-transfer context from this run.

## Hypothesis

- The selected static caller may not be the caller used for the observed `m_FileID`/`m_PathID` transfers, or MuMu's translated code view/lifecycle may invalidate an RVA-based hook after initial installation. These alternatives are not distinguished by this run.

## Blocked

- This experiment does not prove `Material.m_Shader` was read or recover a Shader reference.
- It does not establish Material ownership of the `m_FileID`/`m_PathID` transfers, nor decode a `sourceFile#PathID` pair.
- It does not link a runtime Material/Shader object to a RenderDoc EID.
- An install-success status alone is not evidence that the hooked function executed.

## Next gate

The field-Transfer helper and return-PC semantics are validated for the observed generic PPtr callsites. The likely Material shader-field sequence is also statically isolated, but no runtime call has been observed from it. Next, identify the class/owner boundary that invokes RVA `0x2208798`, and establish a reproducible asset-load transition that executes it. For the next capture, the existing field Transfer observer itself can identify the call through exact `fieldName` and return-PC values; do not rely on a separate caller hook until its code mapping is stable. Require same-thread owner context and a direct relation to the object-level Transfer before interpreting values. If the function does not execute during verified material loading, or runtime code mapping remains unstable, move to guest-side instrumentation instead of repeating screen-only runs.

## Evidence

- `artifacts/HookSessions/hsr45-material-caller-nested-20260917/HSR-4.5-MUMU_20260916_174017983/runtime/events.ndjson`
  - Event sequence 14: serialized field Transfer observer installed, RVA `0x23c77a4`, expected and actual first instruction match.
  - Event sequences 15, 20 and 34: caller observer installation status changes; sequence 15 is installed, sequence 20 refuses cross-architecture, sequence 34 reports instruction mismatch.
  - Event kinds: 336 field Transfer records; 168 each `m_FileID` and `m_PathID`; zero `m_Shader`; zero caller execution events; zero records with `activeSerializedFieldCaller`.
  - Session finalization: 76,777 runtime events; `AS Shader: 0, Material: 0` and `explicit links: 0`.
- `References/TypeTreeRipper/source/android/android_impl.cpp`
  - Lines 7517-7519: Transfer wrapper captures the caller return PC with `__builtin_return_address(0)`.
  - The former `verified-void-material-field-transfer` semantics label was unconditional; new builds use the neutral `verified-void-serialized-field-transfer` label.
- `artifacts/analysis/HSR-4.5/guest/libunity-current2.so`
  - RVA `0x239f4bc`: `BL 0x23c77a4`; return PC `0x239f4c0`.
  - RVA `0x239f588`: `BL 0x23c77a4`; return PC `0x239f58c`.
  - RVA `0x22087fc`, `0x2208830`, `0x22088f8`: calls to `0x23c77a4` in the statically identified `m_Shader`/`m_FileID`/`m_PathID` transfer routine; no matching return PCs were present in this runtime session.
