# HSR 4.5 native Material TypeTree revalidation (R56)

## Result

R56 revalidated a non-empty native Material TypeTree on the current HSR 4.5
build. It did not produce an object-identity bridge. MuMu itself was responsive
throughout; the guest was not restarted at the VM level.

## Confirmed

- The guest log records ClassID 21 `Material` generation through the native
  TypeTree transfer slot at `+0xA8`, then validates 76 nodes and 295 strings.
  Evidence: `artifacts/HookSessions/hsr45-native-material-typetree-r56-current-build-20260918/HSR-4.5-MUMU_20260917_175629857/runtime/typetree/ripper-logcat.txt:9,12,15-16`.
- `release.ttbin` is 7,173 bytes, revision `2019.4.34 Runtime`; the strict
  `typetree` decoder reports one Material tree, 76 nodes, and
  `fieldIntegrity.status=valid`. The decoded tree includes
  `Base.m_Shader.m_FileID`, `Base.m_Shader.m_PathID`,
  `Base.m_ShaderKeywords`, and `Base.m_SavedProperties`.
  Evidence: `artifacts/HookSessions/hsr45-native-material-typetree-r56-current-build-20260918/HSR-4.5-MUMU_20260917_175629857/runtime/typetree/release.typetree-report.json`.
- The `.hook` parses to 135 event records. Its analysis discovers the adjacent
  runtime TypeTree, but reports `identityBridge.status=not_observed`, zero
  serialized-identity records, zero Draw/EID contexts, and zero shared
  RenderDoc event identities.
  Evidence: `artifacts/HookSessions/hsr45-native-material-typetree-r56-current-build-20260918/HSR-4.5-MUMU_20260917_175629857/HSR-4.5-MUMU_20260917_175629857.analysis.json`.
- Transfer observer installation and Material transfer/callsite records are
  present. The recorded callsite module is `/system/lib64/libhoudini.so`; its
  host return PC/RVA cannot be relabeled as an original HSR ARM64 callsite.
  Evidence: `artifacts/HookSessions/hsr45-native-material-typetree-r56-current-build-20260918/HSR-4.5-MUMU_20260917_175629857/runtime/events.ndjson:68-74`.
- At the post-run health check, MuMu host processes responded, ADB serial
  `127.0.0.1:16384` was online, and Android reported boot complete. A later ADB
  screenshot showed the HSR overworld; that screenshot is not joined to R56
  events and is not used to claim R56 captured a particular gameplay action.

## Observed

- R56 adds reproducible Material field-layout evidence, not serialized field
  values. The PPtr's `m_FileID`/`m_PathID` nodes describe schema only.
- The runtime Transfer object pointer, Material type, TypeTree layout, and
  `libhoudini.so` caller evidence coexist in the session, but no same-call
  relation connects them to a serialized `sourceFile#PathID`, a Shader object,
  or a RenderDoc EID.

## Hypothesis

- A guest-side ARM64 callsite token may still be observable independently of
  the host-translated return PC. It must be mapped using the exact HSR 4.5
  module and validated instruction windows; temporal or pointer overlap is not
  an acceptable substitute.

## Blocked / next gate

- Do not treat R56 as an identity success. Before another runtime hook, statically
  and from existing event artifacts determine how the HSR ARM64 Transfer caller
  maps through Houdini, and whether a non-crashing guest-side observation can
  carry the same invocation token to serialized `m_Shader` decoding.
- Keep the R53 opaque-callsite observer disabled until its Houdini crash boundary
  is isolated. Do not restart MuMu when its host and ADB remain responsive.
- End-to-end `sourceFile#PathID -> Material.m_Shader -> Shader object -> RenderDoc
  EID` remains unproven.

## R59: one opaque producer-input probe in an authenticated game scene

### Confirmed

- R59 used the existing MuMu VM (`127.0.0.1:16384`); the host process responded
  and ADB remained online. The earlier apparent freeze was HSR on its title
  entry screen, not a frozen MuMu VM. After entering the game, the overworld
  rendered normally. No VM restart was needed.
- The ARM64 opaque-callsite observer installed with `probeMask=1`; only the
  producer-input site was requested (`producerInputProbeStatus=0`, other probe
  statuses `-2`). Evidence: R59 `runtime/events.ndjson:188`.
- R59 completed with 9,817 event rows: 9,055 resolver candidates, 128 PPtr
  callsite-entry observations, and 98 Transfer events. There was no
  `unity.hsr45.opaque-callsite-probe` callback event, so the requested producer
  input site was installed but not observed executing in this capture.
- R59's summary reported 9,817 runtime records and zero AS Shader, Material, or
  explicit identity links. The 128 PPtr observations and resolver candidates do
  not establish a Material/Shader identity bridge.

### Observed

- The single-site configuration avoided the R53-style guest crash in this run;
  this is a non-crash observation, not proof that the probe is universally safe.
- The capture began at the title screen and was then continued into the
  overworld while the event session was collecting. Thus the lack of a
  producer-input callback is a valid negative observation for this run, but
  does not establish that the callsite is never used by gameplay.

### Hypothesis

- The observed HSR activity may not traverse the selected producer-input site,
  or the site may be reached only by a different resource-loading operation.
  This needs a discriminating trigger and cannot be inferred from event timing
  or neighboring resolver records.

### Blocked / next gate

- Do not expand the probe mask or add probes based only on this negative result.
  First inspect the exact caller/callee control flow around the selected
  producer-input site and identify a concrete in-game operation that should
  traverse it. Then run that one operation with the same single-probe mask.
- The R59 probe has no callback, so it contributes no argument semantics,
  serialized `sourceFile#PathID`, Material field value, Shader object identity,
  or RenderDoc EID relation. The final identity bridge remains unproven.

## R64-R66: entry-only callsite-to-typed-return evidence

### Confirmed

- R64 reused MuMu without restarting the VM; HSR entered the overworld. The
  session completed with 9,806 runtime rows, 9,004 resolver candidates, 128
  PPtr entries, 98 Transfer events, and 32 bounded raw producer observations.
- R64 repeated two exact dynamic PPtr/producer observations on one thread. For
  callsite observation IDs 1 and 2, producer-input SP was exactly `PPtr entry
  SP - 0x60`; raw `x8` equaled the PPtr entry's raw `argument1_32`, and raw
  `x25` equaled its readable `rawKeyWords.word8_64`. The subsequent outer
  resolver candidates at `0x23abd18` returned null. These remain opaque fields.
- R65 recorded 32 raw resolver-argument observations at `0x23a9210`, but the
  diagnostic cap was reached before a typed return could be paired. This
  motivated a same-thread invocation token rather than sequence/time joining.
- R66 statically verified the instruction window: `mov w2,w20` at
  `0x23a9210`, `BL` at `0x23a9214`, and continuation at `0x23a9218`. The
  install record confirms the exact ARM64 window and successful installation
  of only probe mask 8.
- R66's thread-local token was set by that callsite observer and consumed by
  the resolver wrapper only when both arguments matched. One such invocation
  returned an object whose runtime vtable was observed as PersistentTypeID 21
  (`Material`). Evidence: `runtime/events.ndjson:434-435`; install record at
  line 185. The audit reports one verified callsite-to-typed-return relation
  and retains `endToEndIdentityBridge=not_proven`.
- The audit regression accepts this narrow relation, rejects a mismatched
  invocation token, and does not promote it to serialized identity.
  `Test-HSR45OpaqueCallsiteEvidence.ps1` passes.

### Observed

- R66 completed with 10,090 rows: 9,070 resolver candidates, 71 runtime-typed
  Material/Shader returns, 256 bounded raw resolver callsite observations,
  128 PPtr entries, and 98 Transfer events. The tool summary still reports
  zero AS Shader/Material summaries and zero explicit identity associations.
- Exact pointer-value comparison found no overlap between the 71 typed
  resolver return pointers and Transfer `object` fields. This is a scoped
  negative observation, not proof that the objects are semantically unrelated.
- MuMu remained responsive and ADB stayed online. R64-R66 reached the HSR 4.5
  overworld; no MuMu VM restart was required.

### Hypothesis

- The `0x23a9210 -> BL 0x23a9a7c` route returns real Material objects, but is
  not yet shown to be the same instance consumed by Transfer or represented by
  an AS serialized identity. The observed pointer mismatch may indicate
  separate stages, but that interpretation is unverified.

### Blocked / next gate

- No verified call chain connects the typed Material return to a Transfer
  object's serialized field read, including `Material.m_Shader`.
- The R64 PPtr route still resolves to null; it does not bridge the R66 typed
  Material result.
- No `sourceFile#PathID`, Shader object identity, or RenderDoc EID relation is
  accepted. Do not use opaque arguments, host-translated caller addresses,
  pointer reuse across callbacks, event order, or proximity to fill this gap.
- Next inspect the exact HSR 4.5 ARM64 continuation and Transfer field-read
  path. Design a minimal observation carrying a call token from the verified
  Material return into the actual Material field transfer; only then attempt
  to read and verify `m_Shader` in that same invocation.
