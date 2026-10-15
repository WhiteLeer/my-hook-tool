# HSR 4.5 Material Shader Direct-Caller Trigger Audit (R70-R72)

## Conclusion

Two version-matched static callers of the `m_Shader` field-transfer routine
were probed separately. Neither caller was observed executing. R72 exercised
an authenticated character-details flow, switched March 7th to Qingque, and
opened the Skills tab, but still emitted only `m_FileID`/`m_PathID` fields.
These runs reject the tested triggers as evidence for the static field path;
they do not prove the path is unreachable in all resource-loading flows.

## R71: caller at `0x181f7a4`

- Caller observer installation at the ARM64 target reported `installed`, with
  actual first instruction `0xf81e0ffe` (event sequence 14).
- The corresponding x86_64/Houdini view later reported an instruction mismatch
  and decoded a branch thunk to the guest Ripper mapping (event sequence 16).
- The event file has 1,769 records, 151 `m_FileID`, 151 `m_PathID`, zero
  `m_Shader`, and zero `unity.serialized-field-caller` execution events.
- Finalized summary: RTTI catalog 4,684 bytes; AS Material/Shader 0; explicit
  associations 0.

## R72: caller at `0x220854c`

- The ARM64 caller observer reported `installed` with actual first instruction
  `0xf81e0ffe` (event sequence 12). Its later x86_64 view reported a branch
  thunk to the injected guest Ripper (event sequence 16); neither status is an
  execution record.
- The authenticated game reached the character page. The selected character
  changed from March 7th to Qingque, and the Skills tab was opened while the
  capture remained active.
- The event file has 2,227 records, 172 `m_FileID`, 172 `m_PathID`, zero
  `m_Shader`, and zero `unity.serialized-field-caller` execution events. It
  includes four Material and four Shader Transfer-layout records; those are
  not a Material-to-Shader association.
- Finalized summary: RTTI catalog 4,684 bytes; AS Material/Shader 0; explicit
  associations 0.

## Evidence classification

### Confirmed

- The static ARM64 image SHA-256 is
  `3fd4436ae093c58a958ed78c144f198342b51ade083e7dae9f81d905fb2be68f`.
  It contains two direct callers of `0x2208798`: callsites `0x181f7dc` and
  `0x2208584`; `0x2208798` passes the literal `m_Shader` to generic field
  Transfer at `0x22087fc`, return PC `0x2208800`.
- R71's `0x181f7a4` and R72's `0x220854c` caller hooks passed their ARM64
  instruction guards at installation. The event streams contain no caller
  execution event for either run.
- The verified runtime field helper continued to report only PPtr child
  fields in both runs. Caller installation, Material/Shader Transfer records,
  and same-thread co-occurrence do not join a Material to its Shader.

### Observed

- The R72 character-selection trigger did increase the observed Transfer
  surface to include both Material and Shader records, but did not produce the
  statically expected `m_Shader` field callback at return PC `0x2208800`.
- MuMu remained responsive; the game was waiting at the title-entry prompt in
  R71 and was advanced into the overworld. No VM restart was needed.

### Hypothesis

- The two static routines may belong to an offline/asset serialization route
  that is not used by the observed runtime resource-load path, or the relevant
  object graph may be traversed through a different guest-side operation.
  These possibilities remain unproven.

### Blocked

- No runtime Material `m_Shader` PPtr value, exact Material-to-Shader object
  edge, serialized `sourceFile#PathID`, AS identity join, or RenderDoc EID join
  was established by R71/R72.
- R56 remains the evidence for a non-empty, field-valid Material TypeTree;
  R71/R72 only produced the RTTI catalog and do not extend TypeTree field
  coverage.

## Next gate

Stop repeating these two static callers and UI triggers. Trace the actual
version-matched runtime object/load boundary already observed for Material and
Shader, then connect its same-invocation token to a field-level TypeTree
serializer or PPtr resolver. A candidate is admissible only when the event
contains the exact typed owner, `m_Shader` field, raw PPtr value, and an
invocation token from that same call chain. Keep `sourceFile#PathID` and
RenderDoc EID joins blocked until their own direct evidence exists.

## Artifacts

- R71 hook: `artifacts/HookSessions/hsr45-material-inherited-caller-mshader-r71-20260918/HSR-4.5-MUMU_20260917_231039228/HSR-4.5-MUMU_20260917_231039228.hook`
- R71 events: same session's `runtime/events.ndjson`; caller install/mismatch
  records are event sequences 14 and 16.
- R72 hook: `artifacts/HookSessions/hsr45-material-base-caller-character-switch-r72-20260918/HSR-4.5-MUMU_20260917_232344158/HSR-4.5-MUMU_20260917_232344158.hook`
- R72 events: same session's `runtime/events.ndjson`; caller install/mismatch
  records are event sequences 12 and 16.
- R72 finalized analysis:
  `artifacts/analysis/HSR-4.5/r72-character-switch-analysis.json`.
- Static image:
  `artifacts/analysis/HSR-4.5/guest/libunity-current2.so`.
