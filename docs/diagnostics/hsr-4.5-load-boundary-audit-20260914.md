# HSR 4.5 Object Load Boundary Audit

## Scope

This is a read-only audit of the existing TypeTreeRipper source. It does not
change the Hook implementation and does not infer Unity field semantics from
addresses, call order, or values.

## Confirmed

- `source/android/android_impl.cpp:1026-1033` defines the observed Transfer
  ABI wrapper and explicitly keeps the serialization-context arguments opaque.
- `source/android/android_impl.cpp:1099-1101` declares the serialized-identity
  observer, but the observer receives an opaque two-argument function boundary.
- `source/Object.hpp:68`, and the corresponding declarations for the other
  object revisions, expose `VirtualRedirectTransfer(RemapPPtrTransfer&)` as a
  virtual interface. This is a type/interface declaration, not evidence that
  the current Android Hook reaches a serialized-object load callback.
- The current Android implementation installs Transfer, TypeTree and
  serialized-identity observers, but the source contains no implemented
  `PersistentManager`, `SerializedFile`, `LoadObject`, or `ReadPPtr` callback
  that emits `sourceFile + PathID + runtime object handle` together.

## Observed

- The existing runtime event schema records Transfer objects and identity-map
  records separately. No source-level path found in this audit merges them at
  the actual object-load boundary.
- `RemapPPtrTransfer` can describe a serialization operation in the upstream
  object model, but the current project does not establish its concrete guest
  vtable slot or call site for HSR 4.5.
- A read-only `typetree-static` scan of the HSR 4.5 `libunity.so` using the
  literal `m_PathID` produced 128 bounded candidate references. The candidates
  point at several different call targets and are all marked `candidate` by
  the tool; none establishes a function boundary that emits a serialized-file
  identity and a runtime object handle together.

  Evidence: `artifacts/analysis/HSR-4.5/pathid-anchor-20260914.json`,
  `summary.anchorCount=1`, `summary.referenceCount=128`,
  `summary.callWindowCount=128`.
- The ELF section table contains `.dynsym` and `.dynstr`, but no `.symtab`.
  Internal Unity C++ symbols are therefore not available for a name-based
  lookup in this binary. The current static tool can only use literal-string
  references and bounded ARM64 call windows.

## Hypothesis

- The required bridge probably has to be installed at a version-specific
  serialized-object/PersistentManager load boundary, or at a proven callback
  immediately after that boundary. This remains a design hypothesis until a
  runtime event contains all three fields from the same call context.

## Blocked

- No safe, version-verified Android entry point is currently available for the
  next experiment. Calling the existing native TypeTree path from the Transfer
  worker is not a substitute: the prior `0xa0` experiment crashed, and the
  successful `0xa8` TypeTree experiments did not expose object identity.
- Therefore the next execution gate is not another vtable-offset sweep. It is
  a read-only observation of the concrete serialized-object load callback,
  with a success criterion of one event containing `sourceFile`, `PathID`, and
  a runtime object handle, followed by a separately verified same-frame
  RenderDoc EID relation.
- The `m_PathID` string scan is insufficient as that observation: it is a
  search-space reduction only, not permission to install a hook at any of the
  128 candidates.
- Because the internal symbol table is absent, a symbol-name search cannot be
  the next gate. A valid next experiment needs a version-specific call-graph
  boundary plus runtime evidence that the boundary emits the required fields.

## Offline Gate Verification

The existing verifier was run with the HSR 4.4 AS index, the HSR 4.5 Material
TypeTree report, the latest runtime analysis, and the HSR 4.5 profile. With
`Material` as the only critical type, AS identities, Material-to-Shader and
Material-to-Texture bindings, and TypeTree fields all passed. The overall result
was `blocked` solely because `runtimeIdentityBridge=not_observed`.

Evidence: `artifacts/analysis/HSR-4.5/evidence-gate-material-only-20260914.json`,
exit code `2`, with `runtimeIdentityBridge=blocked` and
`typeTreeFields=verified`.

## HSR 4.4 Boundary Evidence

The archived HSR 4.4 session
`HookSessions/_archive/20260904/HSR-4.4-MUMU_20260827_125416111` completed and
contains 404 `unity.transfer` events for `GameObject`. Its event stream has no
TypeTree buffer, serialized-identity event, `sourceFile`, `PathID`, or
RenderDoc EID field. This confirms that the HSR 4.4 path reaches the Transfer
observation layer, but it does not provide evidence that the HSR 4.5 Material
ABI or identity observers are valid for HSR 4.4.

Evidence: `runtime/events.ndjson`, lines 1-404; the first event records
`typeId=1`, `typeName=GameObject`, and opaque arguments only.

The same HSR 4.4 hook was processed by the offline analyzer. It reports 404
records and 404 distinct objects, but only 3 distinct Transfer addresses, with
0 object snapshots and 0 known pointer references. This is a reproducible 4.4
Transfer baseline, not field-level decoding or identity evidence.

Evidence: `artifacts/analysis/HSR-4.4/archived-125416111-analyze-20260914.json`,
`summary.records=404`, `summary.distinctObjects=404`,
`summary.distinctTransfers=3`, `summary.recordsWithSnapshots=0`.
