# HSR 4.5 Character Material/Shader Field Gap (R33-R37)

## Scope

R33-R37 tested the existing serialized-field Transfer observer at
`libunity.so+0x23c77a4` with first instruction `0xf81a0ffe`, filtering
`m_Shader`, `m_FileID`, and `m_PathID`. Runs reused MuMu VM 0 and restarted only
the HSR Android process. No native TypeTree generation was enabled. R35-R37
were performed in the administrative district; R36 opened Character Details,
and R37 also switched the displayed character from March 7th to Qingque.

## Confirmed

- The target process was HSR 4.5.0. MuMu remained ADB-responsive during these
  tests. When the R34 HSR process crashed, the emulator itself was still
  responsive; the game was relaunched for R35 without restarting MuMu.
- R33 emitted 1,332 event rows, including 106 `Material` and 158 `Shader`
  `unity.transfer` calls. It recorded 156 each of `m_FileID` and `m_PathID`,
  and zero `m_Shader` field rows. Runtime `release.ttbin` was 31 bytes.
- R35 visibly switched the overworld character from Qingque to March 7th.
  Its capture contained 118 `Material` and 158 `Shader` Transfer calls, 151
  each of `m_FileID` and `m_PathID`, and zero `m_Shader` rows. Counts are
  observed calls, not counts of unique assets.
- R36 visibly opened March 7th's Character Details screen. It recorded 98
  `Material` and 158 `Shader` Transfer calls, 151 each of `m_FileID` and
  `m_PathID`, zero `m_Shader` rows, and a 31-byte runtime `release.ttbin`.
- R37 visibly switched Character Details from March 7th to Qingque. It
  recorded 230 `Material` and 158 `Shader` Transfer calls, 172 each of
  `m_FileID` and `m_PathID`, zero `m_Shader` rows, and a 31-byte runtime
  `release.ttbin`. The session contains 1,484 valid event rows and no explicit
  AS identity or Material-to-Shader association.
- R34's Android logcat records SIGSEGV in HSR PID 17505. The backtrace includes
  `HookedSerializedPPtr` and `HookedSerializedObjectConsumer`. Both observers
  were enabled in that run. This establishes a crash under that combined
  configuration, but does not isolate which hook or interaction caused it.
- The production HSR-4.5 profile was restored after the experiments, and
  `profile-check` passed without launching MuMu or invoking native ABI code.

## Observed

- The field hook is demonstrably active: it emits named `m_FileID` and
  `m_PathID` rows. The target UI actions also coincide with Material and Shader
  Transfer events, but no owner context or field-level edge connects them.
- Resolver-candidate events remain opaque for this join: `returnCandidate` is
  zero and `returnTypeObserved` is false on sampled rows. The logged 32-bit
  resolver argument is not promoted to a PathID or InstanceID.
- No run in this set yielded non-empty runtime Material/Shader TypeTree nodes,
  sourceFile#PathID, or RenderDoc EID linkage.

## Hypothesis

- The hook at `0x23c77a4` may observe a generic PPtr transfer path that is not
  the producer used for Material's `m_Shader` field, or the field may be read
  through a different call path. The current evidence cannot distinguish
  these explanations.
- The R34 crash may involve overlapping/incorrectly composed generic hooks or
  an ABI/context assumption. Do not repeat the combined broad configuration
  until static call/ABI analysis explains the crash.

## Blocked

- Exact Material object -> serialized `m_Shader` PPtr -> Shader object -> AS
  sourceFile#PathID is still unproven.
- Exact object -> RenderDoc EID remains unproven; no name, address, order,
  low-bit, or ResourceId matching is accepted as a substitute.
- Runtime TypeTree extraction remains empty for Material/Shader in these runs.

## Evidence

- R33 events: `artifacts/HookSessions/hsr45-field-mshader-character-r33-reuse-20260917/HSR-4.5-MUMU_20260917_140552082/runtime/events.ndjson` —
  lines 88/95 observer installation, 233-238 and 303-304 field samples,
  886/919 Transfer samples; 1,332 rows total.
- R34 crash: `artifacts/HookSessions/hsr45-character-screen-mshader-r34-reuse-20260917/HSR-4.5-MUMU_20260917_141502796/runtime/typetree/android-errors-logcat.txt` —
  lines 125, 147, 150, and 154.
- R35 events: `artifacts/HookSessions/hsr45-character-screen-mshader-r35-reuse-20260917/HSR-4.5-MUMU_20260917_141725351/runtime/events.ndjson` —
  lines 99/109 observer installation, 237-242 and 299-300 field samples,
  886/919 Transfer samples; 1,322 rows total. UI evidence:
  `artifacts/analysis/HSR-4.5/r35-switch.png`.
- R36 events: `artifacts/HookSessions/hsr45-character-details-mshader-r36-reuse-20260917/HSR-4.5-MUMU_20260917_142338439/runtime/events.ndjson` —
  lines 96/102 observer installation, 233-238 and 295-296 field samples,
  887/920 Transfer samples; 1,315 rows total. UI evidence:
  `artifacts/analysis/HSR-4.5/r36-character.png`.
- R37 events: `artifacts/HookSessions/hsr45-character-selection-mshader-r37-reuse-20260917/HSR-4.5-MUMU_20260917_143748563/runtime/events.ndjson` —
  lines 89/95 observer installation, 230-235 and 305-306 field samples,
  886/919 Transfer samples; 1,484 rows total. UI evidence:
  `artifacts/analysis/HSR-4.5/r37-character2.png`.
- Profile validation: `my-hook-tool.exe profile-check profiles/HSR-4.5-MUMU.json` —
  passed after restoring production settings.

## Next Gate

Do not repeat the same UI trigger or enable the crashing broad Consumer/PPtr
combination. Statically trace the exact `0x23c77a4` caller and the Material
Transfer path to determine whether `m_Shader` can reach that helper. If not,
identify a separately verified field producer and ABI before adding a narrowly
scoped observer. Then require a same-call-chain Material -> PPtr -> typed Shader
edge before attempting AS identity or RenderDoc EID joins.
