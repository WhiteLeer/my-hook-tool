# HSR 4.5 In-Game Material Field Probe, R2 ABI Correction, 2026-09-17

## Scope

Re-ran the bounded HSR 4.5 observer after correcting the candidate callback ABI
to `void(void*, void*)`, based on the two observed ARM64 argument registers and
static callers that do not consume a return value. MuMu remained running; the
HSR process was relaunched under the guest observer. The title-screen entry
control was triggered during the capture window. This report does not assume
that the selected Material-load callsite executed.

## Confirmed

- Session `HSR-4.5-MUMU_20260917_035126104` completed with 76,757 event records
  and a finalized `.hook`.
- The field observer emitted 312 records: 156 `m_FileID`, 156 `m_PathID`, and
  zero `m_Shader`.
- There were 106 `unity.transfer` records, all typeId 21 (Material), split
  evenly between ARM64 and x86_64 observers. This is not a field-to-object join.
- The ARM64 caller hook at RVA `0x2208798` emitted an `installed` event at
  eventSequence 23. No `unity.serialized-field-caller` execution record exists.
- A later x86_64-side observation at eventSequence 32 reported
  `instruction_mismatch`; its bounded code window begins
  `71e6feb03102069120021fd6`. The event did not report a decoded destination or
  destination mapping. Do not infer callback execution from the earlier
  installation event.
- Every serialized-field event lacks `activeTransferCallChain`,
  `activeSerializedObjectLoadBoundary`, and `activeSerializedFieldCaller`.
- The finalized `.hook` reports `nonEmptyTypeTree=false`. The 4,684-byte runtime
  RTTI catalog is class metadata, not field-level TypeTree output.
- The callback declaration is now `void HookedSerializedFieldCaller(void*,
  void*)` in `References/TypeTreeRipper/source/android/android_impl.cpp`; the
  ARM64, x86_64, and .NET 8 builds succeeded before this run.

## Observed

- ARM64 field events occurred on threads 32313 and 32384. ARM64 Material
  Transfer records occurred on thread 32313, so their time spans overlap.
  Nevertheless, none of the field records contains an active Transfer context
  or owner token. Same-thread/time overlap is not accepted as identity evidence.
- The observed generic field-helper return PCs remain separate from the
  statically identified Material `m_Shader` return PC `0x2208800`.
- The experiment did not decode a Material `m_Shader` PPtr, establish a
  `sourceFile#PathID` relation, or connect an object to a RenderDoc EID.

## Hypothesis

- The statically identified `m_Shader` caller may belong to a load/serialization
  route not exercised by this capture window, or the runtime Material reference
  may be established through another object-loading path. This run does not
  distinguish those alternatives.

## Blocked

- Material object -> `m_Shader` PPtr value and Shader object.
- `sourceFile#PathID` -> runtime Material/Shader object.
- Same-object Material/Shader -> draw -> RenderDoc EID.

## Feasibility assessment and next gate

- Selected HSR 4.5 field TypeTrees remain a demonstrated subgoal from prior
  sessions; this run did not produce one.
- The static Material field path is version-matched, but its selected runtime
  caller route has now failed to produce an execution record in two live
  observations, including one that reached a gameplay scene. Further repetition
  of the same caller hook is not justified. This blocks that specific route,
  not every possible runtime identity route.
- Continue with a read-only call-chain audit of the observed generic PPtr field
  helpers, Material Transfer path, and the actual object-load/identity producer.
  A next runtime probe is allowed only after one boundary is shown to carry
  `sourceFile`, `PathID`, and a runtime object handle in the same call chain, with
  ABI and architecture validated. If the target build exposes no such boundary,
  mark the bridge blocked rather than substitute names, addresses, ordering,
  keys, or RenderDoc ResourceIds.

## Evidence

- `artifacts/HookSessions/hsr45-in-game-mshader-r2-abi-20260917/HSR-4.5-MUMU_20260917_035126104/runtime/events.ndjson`
  - 76,757 records; 312 field transfers (156/156/0); 106 Material Transfers
    (53 per architecture); zero callback executions; zero field events with
    active Transfer/load/caller context; eventSequence 23 and 32.
- `artifacts/HookSessions/hsr45-in-game-mshader-r2-abi-20260917/HSR-4.5-MUMU_20260917_035126104/HSR-4.5-MUMU_20260917_035126104.hook`
  - completed; `nonEmptyTypeTree=false`; no identity links.
- `docs/diagnostics/hsr45-in-game-mshader-r1-20260917.md`
  - prior live gameplay observation; same candidate callback had no execution
    records despite successful patch evidence.
- `References/TypeTreeRipper/source/android/android_impl.cpp`
  - corrected two-argument, void callback ABI and bounded caller event fields.
