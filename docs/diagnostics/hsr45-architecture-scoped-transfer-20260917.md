# HSR 4.5 Architecture-Scoped Transfer Audit, 2026-09-17

## Scope

Read-only audit of the completed session `HSR-4.5-MUMU_20260916_181344716` after adding `runtimeArchitecture` and `monotonicTimeNs` to runtime events. No new MuMu run was started for this audit.

## Confirmed

- The session has 76,774 NDJSON records. Events now distinguish ARM64 and x86_64 and carry a monotonic timestamp; only two records lack the architecture tag.
- `unity.transfer` emitted 104 records, split evenly between ARM64 and x86_64 (52 each). The records include Material typeId 21 and runtime object/Transfer pointers.
- `unity.transfer-context-call` records include typeId 21 and an active-transfer object pointer. In the sampled calls, the ARM64 and x86_64 observers reported the same context, Transfer, and Material object values.
- The filtered serialized-field observer emitted 336 ARM64 records: 168 `m_FileID`, 168 `m_PathID`, and no `m_Shader` record.
- The finalized `.hook` explicitly reports `nonEmptyTypeTree=false` and `offlineBaselineUsable=true`. Its 7,755-byte `rtti-catalog.txt` lists persistent type ID, size, abstract/factory flags and class name; it is not field-level TypeTree data. The session's 31-byte `release.ttbin` does not change that status.
- This `.hook` contains runtime records only; it has no `renderDoc`, `as`, or `links` section. It is therefore not yet a resource-restoration package, even though the session itself finalized successfully.

## Observed

- The complete field-event span is about 21-185 seconds on ARM64 threads 2936 and 2996; Material Transfer spans about 52-159 seconds on thread 2936. Their time ranges and one thread ID overlap, so the earlier inference that they were simply from different threads/times is withdrawn.
- Despite that overlap, all 336 field events lack `activeTransferCallChain` and `activeSerializedObjectLoadBoundary`. The captured field records contain no owner object token, so temporal/thread overlap is not a same-object relation.
- Two captured Material `before_original`/`after_original` intervals contain no filtered field event. This interval check covers only the two recorded pairs, not all Material Transfer calls.
- Exact pointer comparison found no `unity.transfer.object` match in `objectCandidate`, `returnObjectCandidate`, `output`, or `outputValue` fields from the recorded load/consumer candidate events. These candidate fields remain semantically opaque regardless.
- Nine `m_FileID` snapshots (ARM64 eventSequence 75163, 75165, ... 75179) contain the printable literal `PPtr<$Shader>`; their following `m_PathID` records use return PC `0x40002359f58c` on the same stream/thread. This is a directly observed PPtr template token, not a decoded serialized value or owning-object identity.
- Static ARM64 disassembly explains the observed return PCs: RVA `0x239f3f4` constructs `PPtr<...>` from its type metadata and calls the generic field Transfer at `0x239f4bc` (`m_FileID`, return `0x239f4c0`) and `0x239f588` (`m_PathID`, return `0x239f58c`). The separate Material routine at RVA `0x2208798` passes the literal `m_Shader` at `0x22087fc`, whose field-Transfer return PC would be `0x2208800`; that return PC is absent from this session.
- Therefore the observed `PPtr<$Shader>` type agrees with the Material TypeTree's `m_Shader` field type, but the live records do not prove they are owned by a Material or came from the Material `m_Shader` callsite.
- Two events lack `runtimeArchitecture`; they do not affect the Material/field event counts above, but the event writer coverage is not yet universal.
- `runtime-build` records are repeated for x86_64. They are not interpreted as separate game versions or independent successful hook executions.

## Feasibility assessment

- **TypeTree acquisition is demonstrated** for selected HSR 4.5 classes, including non-empty Material and Shader field trees. This proves field-layout knowledge is attainable, not that all classes or runtime instances are decoded.
- **This latest session did not acquire a non-empty runtime TypeTree.** Its final `.hook` marks that explicitly; the valid offline baseline reference and RTTI catalog must not be described as runtime TypeTree success.
- **Material/Shader runtime binding remains technically plausible but unproven.** The static Material field sequence identifies `m_Shader` followed by its PPtr fields, while the live run did not execute the corresponding observed return PCs. The current observer set has not joined that field sequence to a Material instance.
- **`sourceFile#PathID` identity is not yet demonstrated.** Opaque identity-map keys and runtime values remain opaque; the current events do not provide a same-call source-file, PathID, and object-handle tuple.
- **RenderDoc EID linkage is a separate, harder gate.** No event in this session binds the same native Material/Shader object to a draw submission or RenderDoc event. Completing the serialization bridge alone would not prove EID linkage.
- Overall: the end goal is feasible in principle as a sequence of instrumented, falsifiable joins, but there is not yet an end-to-end demonstrated path. The current generic field observer is insufficient; repeating the same scene-only run is not justified.

## Hypothesis

- The observed field calls are generic PPtr serialization from a different owner/path than the statically identified Material `m_Shader` sequence. Runtime execution of that exact sequence has not been observed, so owner identity cannot be assigned.

## Blocked

- No accepted Material object → `m_Shader` reference/value association.
- No decoded `sourceFile#PathID` → runtime Material/Shader object association.
- No same-object Material/Shader → draw submission → RenderDoc EID association.

## Next gate

1. Do not repeat a screen-only capture. First establish, from the live ARM64 code window and a callsite return PC, that the exact `m_Shader` field Transfer sequence executes during a controlled material-load transition.
2. At that sequence, capture a single per-call correlation token plus the enclosing Material Transfer object token on the same architecture/thread. The acceptance record must contain the field name, PPtr values, owning Material token, and call-chain relation; no address proximity, names, counts, or event order may substitute.
3. Only after that join succeeds, add a separate draw-boundary probe that emits the same native object token alongside a RenderDoc event identifier for the same frame. If the code-view/ABI mapping cannot be validated, stop and mark the gate blocked rather than guessing another RVA.

## Evidence

- `artifacts/HookSessions/hsr45-arch-tag-validation-20260917/HSR-4.5-MUMU_20260916_181344716/runtime/events.ndjson`
  - Total records: 76,774; `unity.transfer`: 104 (ARM64=52, x86_64=52); `unity.serialized-field-transfer`: 336 (ARM64 only; `m_FileID=168`, `m_PathID=168`, `m_Shader=0`).
  - Material examples: `unity.transfer` eventSequence 48/75336 and `unity.transfer-context-call` eventSequence 75331-75332 / 43-44; object/context values are recorded in each event.
  - Field examples: eventSequence 54385-54386, ARM64 thread 2996, monotonic time about 21.23s; no owner token.
  - Field spans: ARM64 threads 2936/2996, about 21-185s; Material Transfer spans: ARM64/x86_64 thread 2936, about 52-159s; 336 field events have no active Transfer/load-boundary context.
  - Exact pointer intersection from Material `unity.transfer.object` values into candidate/output fields is zero; two captured Material original-call intervals contain zero filtered field events.
- `artifacts/HookSessions/hsr45-arch-tag-validation-20260917/HSR-4.5-MUMU_20260916_181344716/HSR-4.5-MUMU_20260916_181344716.hook`
  - `session.evidence.nonEmptyTypeTree=false`; `offlineBaselineUsable=true`; top-level payload has runtime `records` but no RenderDoc/AS/links sections; finalized capture status is not equivalent to TypeTree or restoration-package success.
- `artifacts/HookSessions/hsr45-arch-tag-validation-20260917/HSR-4.5-MUMU_20260916_181344716/runtime/typetree/rtti-catalog.txt` and `runtime/typetree/release.ttbin`
  - Catalog has class metadata only; release tree file is 31 bytes.
- `artifacts/analysis/HSR-4.5/guest/libunity-current2.so`
  - RVA `0x239f3f4` constructs the `PPtr<...>` transfer path; `0x239f4bc` and `0x239f588` call the generic serialized-field Transfer for `m_FileID` and `m_PathID`.
  - The Material field path at RVA `0x2208798` passes the static literal `m_Shader` to `0x23c77a4` at `0x22087fc`; runtime return PC `0x2208800` was not observed.
- `artifacts/HookSessions/hsr45-arch-tag-validation-20260917/HSR-4.5-MUMU_20260916_181344716/runtime/events.ndjson`
  - EventSequence 75163-75180: repeated `m_FileID`/`m_PathID` transfers on one stream/thread; `m_FileID` output snapshots expose `PPtr<$Shader>` text, but all field events lack an owning Material Transfer context.
