# HSR 4.5 Material `m_Shader` Corpus and Callsite Audit, 2026-09-17

> Status note: the later session analysis in
> `hsr45-thunk-map-reusevm-r3-20260917.md` supersedes this document's
> statements that the thunk destination was unresolved. The later run maps it
> to our callback implementation, but still records zero callback executions
> and zero `m_Shader` field events.
>
> ABI note: `References/TypeTreeRipper/source/android/android_impl.cpp` was
> subsequently corrected to the statically supported two-argument, void ABI.
> The bounded R2 run in `hsr45-in-game-mshader-r2-abi-20260917.md` still
> records no callback execution or `m_Shader` field event; this report's older
> five-argument prototype description is not current.

## Scope

Read-only cross-check of the saved HSR 4.5 field-transfer sessions and the ARM64
`libunity.so` disassembly. No MuMu process was restarted and no runtime hook was
installed during this audit.

## Confirmed

- The saved artifact corpus contains 24 `events.ndjson` files with at least one
  `unity.serialized-field-transfer` event. An exact search for the emitted
  field-name token `"fieldName":"m_Shader"` returned zero matches.
- Static ARM64 disassembly at `libunity-current2.so` RVA `0x2208798` contains
  a field-transfer call at `0x22087fc`; its arguments are prepared from the
  static literal `m_Shader`. Subsequent calls at `0x2208830` and `0x22088f8`
  transfer the PPtr's `m_FileID` and `m_PathID` fields. These are static facts,
  not evidence that a runtime Material used this path.
- Two static functions call RVA `0x2208798` directly: callsites `0x181f7dc`
  and `0x2208584`. Immediately before each call, the callers move their two
  live arguments into `x0` and `x1`. The available evidence does not identify
  the caller's runtime object as a particular Material instance.
- In session `HSR-4.5-MUMU_20260916_174017983`, the observer targeting
  `0x2208798` first reported `installed` with the expected instruction, then
  reported `instruction_mismatch` at the same RVA in a later process/thread
  initialization. It emitted no `unity.serialized-field-caller` execution
  event. An install event is not execution evidence.
- The mismatch event includes ARM64 bytes `71e6fe90 31823a91 20021fd6` at
  runtime address `0x400023408798`. Decoding them yields `ADRP x17, 0x4000210d4000`,
  `ADD x17, x17, #0xea0`, `BR x17`, hence the branch destination is
  `0x4000210d4ea0`. The same event maps the original RVA into a `libunity.so`
  range beginning at `0x400021af0000`; the branch destination is outside that
  reported range. This establishes a runtime trampoline at the candidate RVA,
  but does not identify the destination mapping or its ABI.

## Observed

- The latest saved sessions continue to show `m_FileID`/`m_PathID` field
  transfers, including snapshots containing the type token `PPtr<$Shader>`,
  but none has an emitted `m_Shader` field event. The type token does not
  establish a Material owner or decode the PPtr value.
- The caller-observer ABI was later corrected to the statically supported
  two-argument, void signature. The unresolved issue is target execution and
  translated-code mapping, not the old five-argument declaration.
- At this turn's live check, ADB reports `com.miHoYo.hkrpg` foreground activity
  `com.mihoyo.combosdk.ComboSDKActivity`; no controlled in-game asset-load
  transition was active. The running MuMu VM was left untouched.

## Feasibility assessment

- **Selected HSR 4.5 field TypeTrees:** demonstrated in prior sessions for
  Material and Shader. This is a feasible, achieved subgoal.
- **Material-to-Shader reference:** plausible because the version-matched
  Material field tree and static transfer routine both describe `m_Shader`,
  but no saved live session has executed and captured that exact field path.
  This remains unproven and is the next gate.
- **Unity `sourceFile#PathID` identity:** not established. The observed PPtr
  template token and opaque runtime key/value fields cannot be reinterpreted as
  serialized identity.
- **RenderDoc EID association:** not established and is a separate downstream
  gate; no common same-object token currently joins a Material/Shader to a draw.
- Overall, the target remains **conditionally feasible, not yet demonstrated
  end-to-end**. The evidence supports continuing only with a narrow live
  experiment that first proves execution of the exact `m_Shader` callsite.

## Blocked

- No runtime `Material object -> m_Shader PPtr value` relation.
- No decoded `sourceFile#PathID -> runtime Material/Shader` relation.
- No same-object `Material/Shader -> draw -> RenderDoc EID` relation.

## Next experiment gate

1. The runtime observer now recognizes only a strict ARM64 `ADRP xN; ADD xN,
   xN, #imm; BR xN` mismatch pattern and emits the computed destination, map
   provenance, executable-range status and a bounded code window. It does not
   hook or modify the destination. Use the next HSR 4.5 run to resolve the
   observed thunk destination `0x4000210d4ea0` to its live mapping and inspect
   its instructions/ABI.
2. Do not reuse the current generic caller-hook ABI or patch the static RVA
   while it resolves to a branch thunk. Find an instrumentation boundary that
   remains stable across the mapping transition, then validate the exact
   `m_Shader` field callsite (`0x22087fc`) and its two direct callers before
   patching anything.
3. Trigger one controlled HSR 4.5 material load/deserialize transition. Accept
   the experiment only if the field Transfer observer records caller RVA
   `0x2208800`, field name `m_Shader`, and the enclosing Material Transfer
   object token on the same architecture/thread/call chain.
4. If the field site does not execute, or the code mapping changes before a
   stable observation, stop this hook route and investigate a guest-side
   serialized-object load path. Do not infer a binding from PPtr type,
   temporal proximity, function installation, names, or pointer similarity.

## Instrumentation change made after this audit

- `TypeTreeRipper/source/android/android_impl.cpp` now adds bounded branch-thunk
  diagnostics to the serialized-field-caller observer's instruction-mismatch
  event. Decoding is limited to the exact `ADRP/ADD/BR` register chain; all
  other mismatches are labeled `not_matching_adrp_add_br`. No destination hook
  or memory write is performed.
- ARM64 and x86_64 Android builds succeeded. The .NET 8 CLI build succeeded
  with zero warnings/errors. Both deployed runtime libraries match the SHA-256
  values in `runtime/HSR-4.5-MUMU/manifest.json`.
- The CLI no longer rejects `--reuse-mumu` with the guest Ripper. Its existing
  `CaptureGuestTypeTree` path sets `wrap.<package>=LD_PRELOAD`, force-stops and
  relaunches only the Android package; the VM itself remains running. The
  status text and help now disclose that app-process restart. The updated CLI
  builds with zero warnings/errors. The experiment script's `-DryRun -ReuseMumu`
  preflight passed for HSR 4.5, class 21, and the `m_Shader,m_FileID,m_PathID`
  field filter without starting MuMu or the Android package. Live execution
  remains unverified.
- R67 verified field-observer emission in HSR but did not observe `m_Shader`.
  Do not treat observer installation or unrelated PPtr child fields as proof
  that the static Material shader-field route executed.

## R67 bounded field-transfer run, 2026-09-18

### Confirmed

- Reused the responsive MuMu VM and relaunched only the HSR Android package.
  The guest package version was verified as 4.5.0. The field-transfer hook was
  installed at ARM64 RVA `0x23c77a4` after its configured instruction guard
  passed. The x86_64 host-side load correctly reported this ARM64 target as
  unsupported; those host events are not treated as guest execution.
- Static disassembly was independently re-read from the SHA-256-identified
  `libunity-4.5.0-arm64.so` (SHA-256
  `3fd4436ae093c58a958ed78c144f198342b51ade083e7dae9f81d905fb2be68f`). At
  `0x2208798`, the routine preserves its first argument in `x20`, forms
  `x22 = x20 + 0x28`, passes the literal `m_Shader` as the field name, and calls
  `0x23c77a4` at `0x22087fc`; the helper return PC for that field is therefore
  `0x2208800`. This confirms static field dataflow only.
- R67 finalized with 934 events, including 98 observed `Material` Transfer
  callbacks and 302 serialized PPtr child-field Transfer events. The latter
  contain exactly 151 `m_FileID` and 151 `m_PathID` records, with zero
  `m_Shader` records.
- The R67 field callback return PCs are not ambiguous: `moduleBase` in the
  observer event is `0x400021200000`, and the saved live process map places the
  caller addresses in HSR 4.5 `libunity.so`. For the frequent
  `m_FileID` return PC `0x40002359f4c0`, the map's file-offset calculation
  resolves to ELF VA `0x239f4c0`; static instruction `0x239f4bc` is a `BL`
  to `0x23c77a4`. The paired `m_PathID` return `0x40002359f58c` resolves to
  ELF VA `0x239f58c`, called from `0x239f588`. These are the PPtr child-field
  sites observed at runtime, not the `m_Shader` return site `0x2208800`.

### Observed, not an identity link

- 200 field events and all 98 Material Transfer events were emitted on thread
  `56002`; another 102 field events were on thread `56050`. Every field event
  lacks `activeTransferCallChain`, and no `m_Shader` event came from return PC
  `0x2208800`. Same-thread membership is not a nested call-chain or object
  identity proof.
- The main menu had to be advanced after the guest app restart. MuMu and ADB
  remained responsive; this was not a MuMu hang. The bounded capture completed
  normally.

## R69 character-screen trigger, 2026-09-18

- Reused the responsive VM, entered the overworld, opened the in-game phone
  menu and character details, then selected a different party character. The
  screenshots confirm the character selection changed while the bounded field
  observer was active.
- R69 finalized with 1,117 events: 230 Material Transfer callbacks and 344
  serialized PPtr child-field events (172 `m_FileID`, 172 `m_PathID`), with
  zero `m_Shader`. No field event had an active Material Transfer call-chain
  context. The 240 field events on thread `60639` and all 230 Material Transfer
  callbacks sharing that thread do not prove object identity.
- Runtime caller PCs again resolve through the saved `/proc/60242/maps` to
  static HSR 4.5 ARM64 ELF addresses. The frequent PPtr field returns resolve
  to `0x239f4c0` and `0x239f58c`; none resolve to the expected Material shader
  field return `0x2208800`. See the R69 process-map artifact for the live
  mapping used in this conversion.
- This is a targeted negative result for the character-page/character-switch
  trigger, not proof that the `m_Shader` route is unreachable in all HSR 4.5
  loading paths.

### Decision and next gate

- Feasibility remains conditional: the exact Material `m_Shader` field route
  exists in the version-matched ARM64 ELF, but this live run did not execute or
  expose it. R67 does not establish a Material-to-Shader PPtr value.
- The observed field helper and PPtr caller sites are now mapped to the
  version-matched ARM64 ELF. The remaining runtime blocker is that actual
  Material Transfer callbacks (vtable slot target `0x22086cc`) and the static
  field serializer at `0x2208798` are distinct paths; character-page loads
  execute the former but did not execute the latter. Trace the caller and
  owner-data flow around both direct callers of `0x2208798` and the guest
  serialized-object loader to find the real route used for `Material.m_Shader`.
  Accept only `m_Shader` at return RVA `0x2208800` with a same-invocation
  Material context and captured PPtr bytes; do not substitute same-thread,
  pointer proximity, or UI timing.

### Evidence

- `artifacts/HookSessions/hsr45-mshader-material-transfer-r67-20260918/HSR-4.5-MUMU_20260917_200539241/runtime/events.ndjson`
  - line 91: ARM64 field observer installed at `0x23c77a4`;
  - line 167: first observed PPtr child-field Transfer record;
  - line 667: first observed Material Transfer record; all 98 are on thread
    `56002`;
  - full-file audit: 934 rows, 302 field Transfer records (151 each
    `m_FileID`/`m_PathID`), zero `m_Shader`; no field record has an active
    Transfer context.
- `artifacts/analysis/HSR-4.5/guest/libunity-4.5.0-arm64.so`
  - `0x2208798` forms `object+0x28`, passes `m_Shader` to `0x23c77a4` at
    `0x22087fc`, and returns at `0x2208800`.
- `artifacts/HookSessions/hsr45-mshader-character-ui-r69-20260918/HSR-4.5-MUMU_20260917_202655452/runtime/events.ndjson`
  - line 97: ARM64 field observer installed; line 223: first PPtr field
    callback; line 668: first Material Transfer callback; full-file counts
    are 230 Material Transfer and 344 child-field callbacks with zero
    `m_Shader` and no active Transfer context.
- `artifacts/HookSessions/hsr45-mshader-character-ui-r69-20260918/HSR-4.5-MUMU_20260917_202655452/runtime/proc-maps-after-capture.txt`
  - lines 594-596: live `libunity.so` map base and file offset used to map
    runtime caller PCs to the static ELF.

## Evidence

- `artifacts/HookSessions/hsr45-material-caller-nested-20260917/HSR-4.5-MUMU_20260916_174017983/runtime/events.ndjson`
  - eventSequence 15: caller observer installed at `0x2208798`;
    eventSequence 34: same configured target reports an instruction mismatch;
    no caller-execution record; 336 serialized-field transfers and zero
    `m_Shader` fields. The mismatch bytes decode to a branch thunk targeting
    `0x4000210d4ea0`, outside the reported `libunity.so` mapping range.
- `artifacts/analysis/HSR-4.5/guest/libunity-current2.so`
  - `0x22087fc` calls the generic field Transfer after preparing `m_Shader`;
    `0x2208830` and `0x22088f8` are the following PPtr child calls;
    direct callsites to `0x2208798` are `0x181f7dc` and `0x2208584`.
- Saved artifact corpus under `artifacts/HookSessions/`
  - 24 event files contain serialized-field Transfer records; exact emitted
    `m_Shader` field-name matches: zero.
- `D:/Unpack_Workspace/References/TypeTreeRipper/source/android/android_impl.cpp`
  - the current field-caller observer uses the two-argument, void prototype;
    R67 confirms generic field-transfer records, but not the exact
    `m_Shader` route.
