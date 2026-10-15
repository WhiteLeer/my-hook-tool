# HSR 4.5 R23 Evidence Audit

## Scope

Audit the R23 runtime session, determine why the opaque-callsite observer did
not install, and assess whether the candidate is worth one controlled retry.
The live MuMu/HSR process was not restarted.

## Confirmed

- Original event stream:
  `artifacts/HookSessions/hsr45-opaque-callsite-r23-live-20260917/HSR-4.5-MUMU_20260917_113129145/runtime/events.ndjson`,
  SHA-256 `20AEE1C86B2E5BB5EADEF5008DE1DA12B6CE3E2ABD091CC2F4120ADBB32C3876`.
- It has 20,798 lines. Lines 97 and 20,730 contain duplicate
  `runtimeArchitecture` properties. The x86_64 unsupported branch wrote a value
  and the common writer appended another. Source now uses `observerArchitecture`
  for the branch-local value; the .NET reader detects duplicate JSON properties
  and isolates malformed rows without editing source NDJSON.
- Copy-only finalization accepted 20,796 records and isolated the two malformed
  rows. The source file and hash were preserved.
- R23 analysis has `identityBridge.status=not_observed`, zero Transfer records,
  zero draw contexts, zero complete typed Material/Shader identities, zero
  RenderDoc EID joins, and no field-level TypeTree output.
- The valid ARM64 opaque-observer record (eventSequence 232) reports
  `instruction_window_mismatch`; it did not install. Source inspection found
  that its full-prologue guard read `OriginalSerializedPPtr` and
  `OriginalSerializedObjectResolveCandidate`. These are Dobby trampoline entry
  pointers, not the original function target sites. The base installers verify
  target instructions before patching and R23 reports both base observers
  installed. This explains the failed comparison as an instrumentation-evidence
  bug, but does not establish that the subsequent probes install or are called.
- The two other base-observer install records were x86_64 unsupported-architecture
  events and contained the duplicate property noted above.
- Installed HSR package version is 4.5.0. ARM64 `libunity.so` is 46,105,336
  bytes, SHA-256
  `3FD4436AE093C58A958ED78C144F198342B51ADE083E7DAE9F81D905FB2BE68F`.
  The same hash appears in historical local copies.
- Static inspection found one `m_PathID` string and no `m_FileID` string. With
  the corrected ELF `.text` VA/file-offset delta of `0x4000`, Capstone confirms
  the PPtr target at RVA `0x23a94bc`, calls to the resolver at `0x23abd00` and
  `0x23abd14`, and another resolver call at `0x23a9214`. The earlier static
  report's zero count used a narrower return-writeback predicate and is not
  evidence that no direct callers exist.
- The live HSR process remained PID 32571 with
  `com.mihoyo.combosdk.ComboSDKActivity` foreground. No process restart occurred.
- Both Android ABIs and the .NET 8 tool built successfully with the current
  guard correction. The ARM64 library deployed to the local runtime directory
  has the same SHA-256 as the ARM64 build output.

## Observed

- The stream is dominated by 16,384 serialized-object load-boundary
  observations, 4,096 consumer observations, and 59 opaque resolver candidates.
  These observations do not prove PathID, InstanceID, typed Material/Shader
  identity, or draw association.
- The original session `.hook` had zero finalized records; copy-only
  finalization made the partial event stream analyzable.
- `Audit-HSR45OpaqueCallsiteEvidence.ps1` accepted zero opaque-to-typed links.

## Hypothesis

- R23's guard failed because it compared trampoline bytes against the original
  prologues. The source correction now records a strict four-instruction
  comparison at each original install site before hooking and gates the opaque
  probes on those stored booleans. A new HSR run must verify this fix.
- Static call-chain evidence makes the candidate plausible, but does not prove
  the 32-bit values' semantics or that they represent serialized PathID.

## Blocked

- No runtime evidence joins serialized `sourceFile#PathID` to a typed Material
  or Shader object.
- No runtime evidence joins Material `m_Shader` to a typed Shader object.
- No RenderDoc EID is joined to either runtime object in the same capture.
- No fresh session has validated the corrected install-site guards, probe
  installation, or probe invocation.

## Feasibility Decision

`technically_plausible_but_unproven`. Historical selected-class TypeTree
captures establish that field-level TypeTree acquisition is possible on HSR
4.5. The corrected guard design plus static call-chain evidence justify one
controlled retry; neither proves the object identity bridge. R23 rejects the
probe installation path as implemented, not the candidate call chain or every
possible identity path.

## Next Gate

At the next controlled HSR launch, require event evidence for the loaded module path, exact
four pre-hook prologue words at both original target sites, unchanged callsite
windows, and successful probe installation. Then require an actual
producer-to-consumer-to-typed-resolver invocation before evaluating identity
semantics. Do not infer PathID or Material/Shader identity from opaque values.
