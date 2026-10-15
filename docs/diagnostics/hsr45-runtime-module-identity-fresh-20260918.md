# HSR 4.5 Fresh Runtime Module Identity Validation

## Scope

Validated the newly built read-only runtime module identity reporter in a fresh
HSR Android process while reusing the existing MuMu VM. No MuMu VM restart was
performed. The run did not enable native TypeTree generation or identity
patches.

## Confirmed

- Session `HSR-4.5-MUMU_20260918_055734252` started HSR PID `23855` through the
  existing VM and produced 77 runtime records.
- `runtime/events.ndjson` line 2, event sequence 1, reports
  `runtime-module-identity`, status `observed`, source `loaded-ELF-PT_NOTE`,
  architecture `x86_64`, and GNU Build ID
  `57ac605d3855a6ce14feefe16b5848e61e01a781`.
- That Build ID matches the freshly built x86_64 Ripper artifact. This closes
  the prior uncertainty about whether the new reporter is present in a fresh
  HSR process.
- MuMu host processes remained responsive; the Android device stayed online.

## Observed, Not Promoted

- The session's `.hook` contains a 4,684-byte runtime RTTI catalog. It has no
  field-level TypeTree, serialized object identity, Material/Shader link, or
  RenderDoc EID association.
- The observed module identity binds the reporter event to a build ID only. It
  does not identify any Unity function, prove a hook invocation, or establish
  object identity.
- The HSR UI remained at its title-entry screen after the bounded run; this
  session did not exercise an in-game material load.

## Blocked / Next Gate

- The end-to-end `sourceFile#PathID -> Material.m_Shader -> Shader -> RenderDoc
  EID` chain remains unproven.
- The repeated static Material callers and character-page/Skills triggers are
  already negative for the `m_Shader` Transfer route. Do not repeat them.
- Next, statically validate an actual runtime Material-to-Shader read boundary
  and its ABI before adding an observer. Any subsequent runtime event must be
  scoped by this exact loaded-module Build ID and retain same-call evidence.

## Evidence

- `artifacts/HookSessions/hsr45-module-identity-fresh-20260918/HSR-4.5-MUMU_20260918_055734252/runtime/events.ndjson:1-2`
  - process 23855 runtime build and observed loaded-ELF Build ID.
- `artifacts/HookSessions/hsr45-module-identity-fresh-20260918/HSR-4.5-MUMU_20260918_055734252/HSR-4.5-MUMU_20260918_055734252.hook`
  - finalized bounded session artifact; runtime catalog only.
- `References/TypeTreeRipper/artifacts/RelWithDebInfoAndroidX8664/libTypeTreeRipper.so`
  - current build corresponding to the observed GNU Build ID.
