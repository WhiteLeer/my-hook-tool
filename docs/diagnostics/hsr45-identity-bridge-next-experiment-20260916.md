# HSR 4.5 Identity Bridge: Next Experiment

## Objective

Demonstrate one direct relation between a real Material Transfer object and a
serialized reference value. This is a prerequisite for any Material-to-Shader
or RenderDoc EID export.

## Existing Evidence Used

- Material Transfer callbacks are real and include the active object and
  Transfer context.
- The Material TypeTree is available as a 76-node runtime schema and names the
  `m_Shader` PPtr fields.
- The PPtr observer has a statically checked boundary and records the two raw
  words read from the guest key, but the accepted sessions did not show PPtr
  nesting inside a Material Transfer callback.
- The object-load/consumer observer now records a same-thread call-chain
  candidate only when both the load output token and returned object candidate
  match exactly.

## Trigger

Run one bounded HSR 4.5 scene session with only the existing safe observers:
Material Transfer, PPtr, object-load boundary, object consumer, and identity
observation. Do not add Vulkan hooks, raw patches, address joins, event-order
joins, or new guessed RVAs.

## Required Record Fields

Every accepted relation must be present in one direct nested call chain and
retain:

- Material object pointer, Transfer pointer, type ID, and caller;
- PPtr manager, key address, raw 16-byte key, result, output slot, and caller;
- serialized-object load/consumer tokens and their exact same-thread match;
- identity function input/output and caller, if the identity call is nested;
- process ID, thread ID, capture session ID, and monotonically increasing event
  sequence for auditability.

The raw fields remain opaque until a version-matched ABI or a serialized-file
decoder proves their semantics.

## Acceptance

Accept only if one event chain directly contains the Material Transfer object,
the PPtr read/result, and a separately evidenced serialized identity tuple. A
RenderDoc EID may be added only if the same capture session exports a direct
runtime callback or shared capture token; matching by object address, name,
order, low bits, hash, or ResourceId is rejected.

## Stop Conditions

- If the streams remain call-chain disjoint, record `blocked` and do not create
  a Material/Shader mapping.
- If the guest crashes, retain the crash as a negative safety result and do
  not increase hook coverage.
- If only TypeTree schema is produced, update schema coverage but do not claim
  field-value or EID progress.
