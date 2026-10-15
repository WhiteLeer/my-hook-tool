# HSR 4.5 Thunk Mapping and Material Field Gate, 2026-09-17

## Scope

Read-only analysis of session `HSR-4.5-MUMU_20260917_031906169`. No new MuMu
launch, package restart, or runtime patch was performed during this audit.

## Confirmed

- The completed session has 75,474 valid NDJSON records and a finalized
  `.hook`. The session completed, but that does not mean the requested identity
  bridge was acquired.
- The ARM64 observer installed a Dobby hook at configured RVA `0x2208798`
  (`eventSequence=17`, `dobbyStatus=0`). This proves hook installation only.
- A later x86_64 observer decoded the ARM64 `ADRP x17; ADD x17,x17; BR x17`
  patch at the same runtime target. Its destination is
  `0x4000210d5400`, mapped to
  `/data/local/tmp/my-hook-typetree-ripper-arm64.so` at file offset
  `0x94400` (`eventSequence=34`). The destination code window matches the
  deployed ARM64 `HookedSerializedFieldCaller` implementation. This proves
  the live target was redirected to our callback, not that the callback ran.
- There are 166 `unity.serialized-field-transfer` records: 83 `m_FileID`,
  83 `m_PathID`, and zero `m_Shader`. There are six `unity.transfer` records,
  all Material typeId 21. There are zero `unity.serialized-field-caller`
  execution records.
- The three Material object transfers are logged once per architecture. The
  ARM64/x86_64 duplicates are not independent Material instances and do not
  create a field-to-object link.
- ADB at audit time reports `com.miHoYo.hkrpg` in
  `com.mihoyo.combosdk.ComboSDKActivity`; no in-game material-load transition
  was active.

## Observed

- The field observer continues to see PPtr child fields through other runtime
  callsites; the exact configured Material `m_Shader` path is not observed.
- The callback is present and the code redirection is real, but its hit count
  is zero in this session. Thus “hook installed” and “target function
  executed” are separate states, and only the former is established.
- Existing selected HSR 4.5 field TypeTrees for Material and Shader remain a
  completed subgoal. This session's `.hook` has `nonEmptyTypeTree=false`; it
  did not acquire a field-level TypeTree itself.

## Hypothesis

- The Material field path at RVA `0x2208798` may only execute during a genuine
  in-game asset/material deserialize transition, which this SDK/login session
  did not exercise. Alternatively, observed Material transfers may use a
  different field path. Neither explanation is confirmed.

## Blocked

- No `Material object -> m_Shader PPtr` same-call relation.
- No serialized `sourceFile#PathID -> runtime Material/Shader` relation.
- No same-object Material/Shader-to-RenderDoc EID relation.

## Feasibility assessment

- **Selected HSR 4.5 TypeTrees:** feasible and already demonstrated for the
  requested Material, Shader, and Light classes. This does not establish
  complete TypeTree coverage for every class.
- **Material-to-Shader:** technically plausible and still worth one controlled
  experiment because both the Material field schema and the static field path
  exist, and the runtime hook reached its callback patch. It is not yet
  runtime-proven. Repeating an SDK/login-only run has no value; a real scene or
  material-load transition is required.
- **Unity serialized identity:** feasible only if a live load boundary exposes
  source file, PathID, and the resulting runtime object in one causal call
  chain. Current opaque identity records do not supply that proof.
- **RenderDoc EID:** a downstream, separate join. It remains feasible in
  principle if the same runtime object token can be observed through the draw
  submission path, but no evidence currently demonstrates that token survives
  to RenderDoc's event/resource records.
- **Overall:** not disproven, but end-to-end feasibility is still
  conditional. The current evidence supports one bounded gameplay-transition
  experiment, not broad hook expansion. If the callback remains unhit during a
  controlled material load, retire this RVA route and inspect the actual live
  Material transfer call chain before modifying more code.

## Next gate

1. Start from an actual HSR 4.5 in-game scene, not the SDK/login screen.
2. Capture only the already-configured Material and field observers; do not
   change RVAs, ABI declarations, or add new hooks in that run.
3. Pass only if `unity.serialized-field-caller` executes and records the
   expected caller site, and the field observer records `m_Shader` nested
   under the same Material transfer token/call chain.
4. If only Material Transfer events recur, or if the callback hit count stays
   zero, stop this route. Do not promote name, timing, address, PPtr type,
   pointer proximity, or event order into identity.
5. Only after the Material-to-Shader join passes should the next experiment
   address `sourceFile#PathID`; RenderDoc EID correlation comes after both.

## Evidence

- `artifacts/HookSessions/hsr45-thunk-map-reusevm-r3-20260917/HSR-4.5-MUMU_20260917_031906169/runtime/events.ndjson`
  - eventSequence 17: ARM64 hook installation succeeded at RVA `0x2208798`.
  - eventSequence 34: x86_64 decoded the patched ARM64 thunk and mapped its
    destination into the deployed Ripper library.
  - 75,474 records; 166 serialized-field transfers (`m_FileID=83`,
    `m_PathID=83`, `m_Shader=0`); six Material Transfers; zero caller execution
    events.
- `artifacts/HookSessions/hsr45-thunk-map-reusevm-r3-20260917/HSR-4.5-MUMU_20260917_031906169/HSR-4.5-MUMU_20260917_031906169.hook`
  - completed session artifact; runtime TypeTree remains empty.
- `References/TypeTreeRipper/source/android/android_impl.cpp`
  - `AppendArm64BranchThunkEvidence`, `InstallSerializedFieldCallerObserver`,
    and `HookedSerializedFieldCaller`; destination function is our own callback.

