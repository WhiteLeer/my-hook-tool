# HSR 4.5 In-Game Material Field Probe, R1, 2026-09-17

## Scope

The HSR app was relaunched under the existing observer configuration while
keeping the MuMu VM running. The title screen was entered and the game reached
the administrative-district gameplay scene during the capture window. No hook
ABI, RVA, or observer implementation was changed for this run.

## Confirmed

- Session `HSR-4.5-MUMU_20260917_033535046` completed with 76,763 valid event
  records and a finalized `.hook`.
- The serialized-field observer emitted 312 events: 156 `m_FileID`, 156
  `m_PathID`; it emitted zero `m_Shader` fields.
- The field snapshots include nine occurrences of the printable type token
  `PPtr<$Shader>`. This identifies a template/type token in bounded memory
  snapshots, not a Material owner or decoded PPtr value.
- There are 110 `unity.transfer` records, all typeId 21 / Material. They
  include ARM64 and x86_64 observer records; they do not by themselves associate
  any field event with a particular Material object.
- The configured ARM64 hook at RVA `0x2208798` installed successfully
  (`eventSequence=20`, `dobbyStatus=0`). The x86_64 observer later decoded the
  live ARM64 branch thunk (`eventSequence=34`) to
  `0x4000210d5400`, in the deployed Ripper library at file offset
  `0x94400`. The bounded code window matches our `HookedSerializedFieldCaller`
  callback.
- Despite the successful redirection, there are zero
  `unity.serialized-field-caller` execution records.
- None of the 312 field events carries `activeTransferCallChain`,
  `activeSerializedObjectLoadBoundary`, or `activeSerializedFieldCaller`.
  The field stream therefore remains unjoined to the Material Transfer stream.
- The finalized `.hook` reports `nonEmptyTypeTree=false`; this run did not
  acquire a field-level TypeTree. Previously validated HSR 4.5 Material,
  Shader, and Light TypeTrees remain separate prior-session evidence.

## Observed

- Moving from title/login into a real loaded gameplay scene increased the
  field/Material observations but did not execute the selected static
  `m_Shader` caller or produce the field name `m_Shader`.
- The generic field observer's runtime caller RVAs normalize to
  `0x239f4c0` / `0x239f58c` for most `m_FileID` / `m_PathID` pairs. The
  configured candidate at `0x2208798` is a different call path and has no
  execution record in this run.
- Both architecture streams can report the same Material Transfer address
  token in paired records. This report does not use those repeated values,
  timestamps, or thread overlap to join Transfer and field events.

## Hypothesis

- The current candidate RVA is not reached by the Material serialization path
  exercised in this gameplay scene, or the `m_Shader` field is transferred by
  another runtime path. The available records do not distinguish these.

## Blocked

- Material-object ownership for any captured PPtr field value.
- Runtime `Material.m_Shader` PPtr value and decoded `sourceFile#PathID`.
- A same-object Material/Shader-to-RenderDoc EID relation.

## Feasibility reassessment

- Selected field-level TypeTree extraction for Material, Shader, and Light is
  proven for HSR 4.5; complete all-class coverage is not proven.
- Obtaining Material-to-Shader remains plausible in principle because the
  field schema exists and a static serialization path exists. However, the
  specific configured caller-hook route is now **not demonstrated even in a
  live gameplay scene**. Its successful patch installation is not evidence of
  a hit. Repeating the same capture is not justified.
- The next useful work is a static cross-reference/call-chain audit starting
  from the actually observed field-helper callers (`0x239f4c0` / `0x239f58c`)
  and the observed Material Transfer vtable path. Select a new runtime boundary
  only if that audit proves its arguments and causal relation. If no
  version-matched path connects the Material Transfer to serialized PPtr
  values, mark this bridge route blocked rather than expanding probes.
- `sourceFile#PathID` and RenderDoc EID are downstream gates. Their end-to-end
  feasibility remains conditional; current evidence neither proves nor
  disproves a distinct direct load/draw identity route.

## Evidence

- `artifacts/HookSessions/hsr45-in-game-mshader-r1-20260917/HSR-4.5-MUMU_20260917_033535046/runtime/events.ndjson`
  - 76,763 lines; 312 field transfers; 156 each `m_FileID` and `m_PathID`;
    zero `m_Shader`; 110 Material Transfer observations; nine
    `PPtr<$Shader>` template-token snapshots; zero caller execution events;
    zero field events with active Transfer/load/caller context.
  - eventSequence 20: ARM64 Dobby installation; eventSequence 34: x86_64
    decodes the ARM64 thunk destination and mapping.
- `artifacts/HookSessions/hsr45-in-game-mshader-r1-20260917/HSR-4.5-MUMU_20260917_033535046/HSR-4.5-MUMU_20260917_033535046.hook`
  - completed; `nonEmptyTypeTree=false`; no identity links.
- `References/TypeTreeRipper/source/android/android_impl.cpp`
  - current field observer emits Transfer/load/caller context only when those
    thread-local contexts are active; none was present on this session's field
    records.
