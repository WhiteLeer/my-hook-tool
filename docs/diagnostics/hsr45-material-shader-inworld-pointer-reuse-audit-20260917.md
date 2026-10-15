# HSR 4.5 Material/Shader In-World Pointer Audit, 2026-09-17

## Scope

Test whether the load-return-to-consumer candidate chain from R2 can be typed
by observing both Material (ClassID 21) and Shader (ClassID 48) Transfer
callbacks in one HSR 4.5 session. No TypeTree factory call, guest memory write,
or RenderDoc capture was used.

## Confirmed

- The target package was HSR 4.5.0 on Unity 2019.4.34f1. Guest class IDs were
  limited to `21,48`; the generated runtime config contains `class_ids=21,48`.
- Both Transfer observers installed. In the in-world session, the callback
  recorded 53 unique Material object pointers and 79 unique Shader object
  pointers. The Shader callback records carry `typeId=48`, `typeName=Shader`,
  and vtable `0x400023aa0de8`; Material records carry `typeId=21` and vtable
  `0x400023a82480`.
- The session contains 971 consumer records with a nested load-boundary chain.
  Each of the 54 unique candidate addresses that later appears as a Shader
  Transfer object is present in the same process, session, and thread. The
  consumer records have `directCallerMatch=true`.

## Observed

- The 54-address intersection produces 108 same-process/session/thread pairs
  because the Shader Transfer callback records the same objects more than once.
- Every matched Shader Transfer occurs 115,690 to 122,772 ms after its consumer
  candidate event. The nearest pair is consumer `eventSequence=3910` and
  Shader Transfer `eventSequence=75210`, separated by 115,690.427 ms.
- The consumer-time first-word sample (`objectVtable`, when readable) never
  equals the known Shader Transfer vtable. In some records it is absent; in
  readable examples it is a different value. This field is a raw first-word
  observation, not a validated vtable.
- The finalized session reports `nonEmptyTypeTree=false`, no Shader/Material
  asset identities, and no RenderDoc EID join. The runtime RTTI catalog lists
  Material and Shader class metadata, but is not a field-level TypeTree.
- The session did reach the HSR in-world scene during capture. The captured
  candidate events precede the later Shader Transfer callbacks by about two
  minutes; therefore reaching the world does not make the address intersection
  a same-live-object proof.

## Hypothesis

- The pointer intersection may be allocator address reuse, or the candidate may
  be an opaque non-Unity wrapper whose address is later reused. Available
  records cannot distinguish these explanations.

## Blocked

- Do not accept the 54 address matches as a Material/Shader identity bridge.
- No `sourceFile#PathID` relation is established for these candidates.
- No same-object Material/Shader to RenderDoc EID relation is established.
- The experiment does not prove the end-to-end goal impossible. It confirms
  that typed Material and Shader Transfer callbacks are observable in-game,
  while this load/consumer candidate path is not temporally/type-consistent
  enough to join them.

## Next Gate

Do not repeat the same cold-start capture. First specify an in-world trigger
that causes a fresh serialized-object load after the scene's Shader objects
are already live. Accept a candidate-to-Transfer relation only when the
same-process/session candidate has contemporaneous class evidence (the
candidate-time first word must match a verified class vtable, or another
independent runtime type check) and the exact object pointer is observed by
the typed Transfer callback without a long lifetime gap. Then separately
prove the serialized file/path fields at a version-matched read boundary.
RenderDoc EID correlation remains a later gate requiring same-frame evidence.

## Evidence

- `artifacts/HookSessions/hsr45-material-shader-inworld-r4-20260917/HSR-4.5-MUMU_20260917_043158081/HSR-4.5-MUMU_20260917_043158081.hook`
  - 76,609 records; 264 `unity.transfer` records; 53 unique Material and 79
    unique Shader pointers; 971 linked consumer records; 54 unique candidate
    pointer intersections; 108 same-thread/process/session pairs; zero
    candidate-time first-word equality to the Shader vtable.
- `artifacts/HookSessions/hsr45-material-shader-inworld-r4-20260917/HSR-4.5-MUMU_20260917_043158081/HSR-4.5-MUMU_20260917_043158081.analysis.json`
  - identity bridge remains `not_observed`; no field-level runtime TypeTree.
- `artifacts/HookSessions/hsr45-material-shader-inworld-r4-20260917/HSR-4.5-MUMU_20260917_043158081/runtime/during-screen-6.png`
  - guest screenshot confirming the in-world scene was reached during the
    observation window; not used as object identity evidence.
- R3 control: `artifacts/HookSessions/hsr45-material-shader-class-bridge-r3-20260917/HSR-4.5-MUMU_20260917_042353159/HSR-4.5-MUMU_20260917_042353159.analysis.json`
  - startup/login-stage control observed no Shader Transfer callbacks.

## Follow-up: Party-Switch and Resolver Filtering, R5-R8

### Confirmed

- R5-R7 all hit the configured serialized-object observer ceilings. R7 hit
  65,536 load-boundary calls, 65,536 resolver calls and 16,384 consumer calls;
  each emitted an explicit call-limit event. Raising these limits alone did not
  isolate useful identity evidence.
- R7's resolver observer independently classified 36 returned candidates by
  vtable evidence already associated with typed Transfer callbacks: 9 Material
  records and 27 Shader records. These were candidate objects, not serialized
  asset identities.
- The R7 typed candidates had zero direct PPtr-to-resolver call-chain records.
  The analysis still reports `identityBridge.status=not_observed`, zero
  complete Material/Shader identities and zero shared RenderDoc EIDs.
- R8 changed only observation volume/selection after a successful native build.
  Resolver scanning reached 9,144 calls without hitting its 262,144-call limit;
  emitted resolver records were restricted to typed Material/Shader candidates
  or direct PPtr chains. The overall runtime log was about 76.6 MB, versus
  153.4 MB in R7.

### Observed

- R7 had 27 typed Shader-candidate/Transfer pointer-value intersections. The
  first candidate group was at event sequences 119380-119388; matching typed
  Transfer records were at 177817-177825, about 166.6 seconds later and on a
  different thread. This is an address intersection, not accepted identity.
- R8 had 32 typed-candidate/Transfer pointer-value intersections, all Shader.
  Candidate event sequences 90911-90919 matched Transfer sequences 91932-91940
  with about 330.8 seconds of monotonic-time separation and different threads.
  Additional same-thread matches were separated by about 338.5-346.2 seconds.
- R8 still hit the load-boundary and consumer record ceilings (16,384 and
  8,192). Resolver filtering reduced its own output, but did not turn the other
  observers into a useful source/path identity chain.
- R6-R8 were HSR 4.5.0 / Unity 2019.4.34f1, package `com.miHoYo.hkrpg`,
  guest ABI x86_64 with ARM64 guest observers successfully installed. Party
  switching was performed in the in-world scene; screenshots are UI stimulus
  confirmation only, not identity evidence.

### Hypothesis

- Repeated candidate/Transfer address values could be allocator reuse or a
  long-lived cached object. The multi-minute gaps and thread changes make the
  intersection unsuitable as a same-live-object proof; the current evidence
  cannot choose between those explanations.
- A typed resolver result is a promising instrumentation seam, but its
  `argument1_32` and `argument2_32` semantics remain opaque. They must not be
  relabeled as PathID, InstanceID, key, or any asset identifier without
  caller-dataflow evidence.

### Blocked / Feasibility Assessment

- It is **not yet evidence-supported to claim the end-to-end goal is feasible**.
  The experiments prove runtime visibility of typed Material/Shader transfers
  and typed resolver candidates, but not a semantic serialized identity field.
- No field-level runtime TypeTree, `sourceFile#PathID`, direct PPtr-to-typed
  candidate chain, or RenderDoc EID bridge was obtained in R5-R8. The available
  RTTI catalog is not a field-level TypeTree.
- The goal is not disproven. The next bounded action is static ARM64 callsite
  analysis for resolver caller RVA `0x23a9218`: trace the origin and use of its
  two resolver arguments, then design one runtime record at a version-checked
  same-call boundary. Do not run another cold-start capture until that dataflow
  gate is satisfied.

## Follow-up: HSR 4.5 ARM64 Resolver Caller Trace

### Confirmed

- The guest `libunity.so` pulled from the installed HSR 4.5.0 package has SHA-256
  `3fd4436ae093c58a958ed78c144f198342b51ade083e7dae9f81d905fb2be68f`; its
  hash matches the file still on the MuMu guest at audit time. The target RVA
  `0x23a9218` maps into an executable `PT_LOAD` segment of this exact binary.
- The hook's return address `0x23a9218` is immediately after the `BL` at
  `0x23a9214`, which calls `0x23a9a7c`. The caller passes `x19` in `x0`,
  `w21` in `w1`, and `w20` in `w2`; these registers were populated from the
  caller's incoming `x0`, `w1`, and `w2`, respectively.
- One direct ARM64 `BL` caller of function entry `0x23a9118` was found at
  `0x23a9614`. It passes `x19`, `w8`, and `w20`; the `w8` value is the return
  from `0x23bfbe8`, which receives a context pointer and a 16-byte stack record.
- The routine at `0x23bfbe8` reads a 32-bit word and a 64-bit word from that
  record, looks up/creates an internal tree entry, assigns/stores a 32-bit
  value in the entry, and returns that value. The caller then supplies this
  returned value to the `0x23a9118` lookup. This describes observed dataflow;
  it does not name the record fields as Unity asset identifiers.
- `0x23a9a7c` uses the 32-bit `w1` value in a hash/cache lookup and returns a
  pointer. The call chain therefore has an interned/generated 32-bit key before
  the resolver, not a statically demonstrated `sourceFile#PathID` pair.

### Observed

- Static direct-call scanning found the caller at `0x23a9614`; the narrow
  disassembly windows are preserved in
  `artifacts/HookSessions/hsr45-material-shader-party-switch-r8-20260917/`.
- The verified binary was read from
  `%TEMP%\hsr45-r8-libunity.so` and was not copied into the repository. Small
  ELF headers and caller-window evidence are retained beside the R8 session.

### Hypothesis

- This chain could be an internal resource/object cache path, but its observed
  generated key and lack of semantic field names do not prove that it is the
  Unity serialized-object identity path used for Material/Shader references.

### Blocked / Feasibility Assessment

- This static trace narrows the seam but does **not** establish an identity
  bridge. The 32-bit resolver key must remain opaque; it is not accepted as
  PathID, InstanceID, or source-file index.
- The overall target remains technically plausible, not yet demonstrated:
  selected field-level TypeTrees and typed Material/Shader runtime transfers
  exist, but this call chain currently lacks the semantic source fields needed
  to bind them. The RenderDoc EID join is a later independent gate.
- Do not add another generic resolver hook or repeat party-switch capture.
  The next useful experiment is a narrowly version-gated observation of the
  16-byte record producer at `0x23bfbe8`: capture its two input words, assigned
  32-bit return key, resolved pointer and independently verified runtime type
  in the same call chain. Accept it as serialization identity only if the
  inputs can be directly matched to a real HSR 4.5 serialized PPtr/file record
  using field-level evidence; otherwise reject this seam and pivot to an
  independently identified PPtr deserialization boundary.

### Evidence

- `artifacts/HookSessions/hsr45-material-shader-party-switch-r8-20260917/libunity-elf-head.bin`
  and `libil2cpp-elf-head.bin`: ELF headers and segment mapping; the companion
  `libunity-caller-window.bin` stores the small disassembly source window.
- Exact installed binary identity: SHA-256 above; LLVM `objdump` windows at
  RVA `0x23a8d80-0x23a9230`, `0x23a9570-0x23a9670`, and
  `0x23bfbe8-0x23bff04`.

## Follow-up: PPtr Output-Slot Correlation, R9-R13

### Confirmed

- The hook previously compared the PPtr function's 64-bit return value to the
  resolver's 32-bit argument. Static caller code shows the resolver argument is
  loaded from the PPtr output slot; these values are not interchangeable. The
  comparison now reads that exact output slot and compares it to the resolver
  argument. The function return is retained only as a separate opaque field.
- Static callsite evidence identifies two resolver paths: one at return RVA
  `0x23abd18` after a PPtr call at `0x23abd00`, and an internal resolver call
  returning at `0x23a9218`. The HSR 4.5 profile now records the latter
  independently instead of deriving it as a fixed delta from the outer PPtr
  callsite.
- PPtr event logging is restricted to the configured outer PPtr caller when
  the typed resolver observer is enabled. R10/R11 logs fell to about 4.6 MB
  and 4.6k records, versus about 52 MB and 72k records in R9.
- The corrected native x86_64/ARM64 libraries and .NET 8 tool built with zero
  warnings and zero errors before R10 and R11. Both short MuMu VM-reuse sessions
  completed without a guest crash.
- R12's apparent 33 direct links were a false positive: the correlation audit
  showed the thread-local PPtr context was no longer pending when the resolver
  candidate was observed. The stack slot had been reused, so equality alone
  did not prove a single live PPtr operation.
- R13 tied pending state to the PPtr wrapper lifetime and required a matching
  active PPtr context (manager, key, output slot, and caller) before emitting a
  direct chain. Native x86_64/ARM64 and .NET 8 builds passed with zero warnings
  and errors.

### Observed

- R9: 72,265 runtime records; 65,536 PPtr events (observer ceiling), 29 typed
  resolver candidates, zero direct PPtr-to-resolver records. It used the
  corrected output-slot comparison but did not yet use the independently
  configured nested resolver caller.
- R10: 4,618 records; 989 PPtr records from the filtered caller, one typed
  Material resolver candidate, zero direct chains. The capture included 33
  observed Material and 2 Shader type counts in the analysis catalog, but the
  typed candidate did not carry a PPtr join.
- R11: 4,622 records; one typed Material resolver candidate at runtime return
  RVA `0x23a9218`; `pptrCorrelationAudit.pending=false`, and direct-chain count
  remained zero. Thus the expected PPtr context was not active at that
  candidate observation. The session contains only an RTTI catalog, not a
  field-level TypeTree.
- R12: 3,661 records and 33 typed resolver records appeared to match PPtr
  output-slot values. Retrospective inspection found every candidate had
  `pptrCorrelationAudit.pending=false`; the PPtr state outlived its callback
  scope while the stack slot was reused. These are rejected false positives,
  not identity links.
- R13: 3,641 records and 13 typed resolver records; zero direct chains after
  the live-scope guard. Candidate audits report `pending=false`,
  `configuredPPtrOrigin=false`, `managerMatches=false`, and
  `activePPtrContextMatches=false`. This rejects the tested
  `0x23abd04 -> 0x23a9218` branch as a usable live identity chain under the
  observed workload.
- These short captures started the HSR 4.5 process under the guest Ripper, but
  MuMu did not confirm the game foreground state. They establish observer
  behavior and call records, not a successful in-world identity experiment.

### Hypothesis

- The Material candidate at `0x23a9218` may be reached through another caller
  or a later resolver operation than the filtered PPtr event. The current
  evidence does not establish that both callbacks belong to one live PPtr
  operation; thread-local context absence is evidence against accepting that
  candidate as a direct join.

### Blocked / Feasibility Assessment

- The output-slot equality correction is a code-level repair, not proof that
  the HSR 4.5 serialization identity bridge works. R9-R13 produced zero
  accepted live direct links, zero `sourceFile#PathID` identities and zero
  RenderDoc EID joins; R12's apparent links are explicitly rejected as stale
  context/stack-slot reuse.
- End-to-end feasibility remains **unproven**. Selected TypeTrees and typed
  Material/Shader observations make the target technically plausible, but the
  tested PPtr/resolver branch is not a valid identity bridge. This rejects one
  branch, not the overall goal.
- Do not loosen checks or infer identity from matching values, names, addresses,
  event order, or RenderDoc ResourceId. Do not repeat the rejected
  `0x23abd04 -> 0x23a9218` join. Next, statically trace the actual caller/data
  producer for `0x23a9218`, or pivot to an independently identified serialized
  object deserialization boundary. Before another MuMu run, specify the ABI,
  semantic input fields, and falsifiable success criterion. If no such boundary
  can be identified in the installed 4.5 binary, record this route as blocked
  rather than adding another generic hook.

### Evidence

- R9 analysis: `artifacts/HookSessions/hsr45-pptr-output-slot-r9-20260917/HSR-4.5-MUMU_20260917_060619981/HSR-4.5-MUMU_20260917_060619981.analysis.json`
  - `summary.kindCounts`; 65,536 PPtr candidate events; 29 typed resolve
    candidates; zero direct chain records.
- R10 analysis: `artifacts/HookSessions/hsr45-pptr-nested-caller-r10-20260917/HSR-4.5-MUMU_20260917_061808266/HSR-4.5-MUMU_20260917_061808266.analysis.json`
  - `summary.kindCounts`; 989 filtered PPtr events; one typed Material resolve
    candidate; zero direct chain records.
- R11 analysis: `artifacts/HookSessions/hsr45-pptr-correlation-audit-r11-20260917/HSR-4.5-MUMU_20260917_062250342/HSR-4.5-MUMU_20260917_062250342.analysis.json`
  - `serializedObjectResolveCandidate.records[0].pptrCorrelationAudit`:
    pending=false; resolver caller `0x4000233a9218`; zero direct chain records.
- R12 analysis: `artifacts/HookSessions/hsr45-pptr-all-static-callers-r12-20260917/HSR-4.5-MUMU_20260917_063224214/HSR-4.5-MUMU_20260917_063224214.analysis.json`
  - 33 apparent links are rejected: candidate `pptrCorrelationAudit.pending=false`,
    demonstrating stale context/stack-slot reuse.
- R13 analysis: `artifacts/HookSessions/hsr45-pptr-active-scope-r13-20260917/HSR-4.5-MUMU_20260917_063756357/HSR-4.5-MUMU_20260917_063756357.analysis.json`
  - 13 typed resolver records; `directPPtrToResolveChainCount=0`; sampled
    candidates have no live PPtr context.
- Runtime instrumentation: `References/TypeTreeRipper/source/android/android_impl.cpp`;
  profile wiring: `profiles/HSR-4.5-MUMU.json`,
  `src/MyHookTool/Program.cs`, and
  `tools/Run-HSR45NativeMaterialExperiment.ps1`.

### Follow-up: Sequential PPtr Handoff and Runtime TypeTrees, R14-R17

#### Confirmed

- Static ARM64 disassembly identifies the relevant sequential callsite as
  PPtr return caller `0x23abd04` followed by resolver return caller
  `0x23abd18`. The caller stores the PPtr output slot at `[sp+0x20]` and loads
  that slot into resolver argument `w1`. This is a verified data handoff, not
  proof that the record words mean FileID/PathID.
- The R13 active-wrapper rule cleared correlation when the PPtr wrapper
  returned. That was too early for the statically verified sequential pair;
  it created a false negative for this pair. It does not rehabilitate R12's
  apparent links, which had `pending=false` and stale stack-slot reuse.
- R15 captured 994 exact output-slot-to-resolver-argument handoffs at return
  caller `0x4000233abd18`, from 994 valid PPtr candidates. The configured
  `0x23abd04 -> 0x23abd18` site pair and same-manager check matched. The run
  did not identify those resolver results as Material/Shader; its sole typed
  Material candidate came from the different caller `0x23a9218`.
- R16 obtained a non-empty runtime Material TypeTree (76 nodes; 7,173-byte
  `release.ttbin`) with valid field integrity. Its canonical node JSON exactly
  matched the existing HSR 4.5 baseline (SHA-256
  `2c2db04fe7cce4109d37fc3c3482fffc277a5734579359f55c842e49eb439eba`).
  The Material tree includes `Base.m_Shader.m_FileID`,
  `Base.m_Shader.m_PathID`, shader keywords, and saved properties.
- R17 obtained a non-empty runtime Shader TypeTree (1,551 nodes; 150,991-byte
  `release.ttbin`) with valid field integrity. Its canonical node JSON exactly
  matched the existing HSR 4.5 baseline (SHA-256
  `58a5408b1cae538cb216671bf410f5158c0e336f5556c03cff9f7e6a586c9515`).
- Both runtime trees were successfully attached to finalized `.hook` files.
  This validates TypeTree capture, decoding, baseline comparison, and hook
  packaging independently for Material and Shader.

#### Observed

- R16: `distinctObjects=3`, `distinctTransfers=2`, 275 direct PPtr/resolver
  handoffs, but `serializedObjectLoadToTransferSameObjectCount=0`,
  `serializedIdentityCallChainCount=0`, and no RenderDoc EID join.
- Independent exact-pointer recheck of R16's raw `events.ndjson`: six
  `unity.transfer` records identify three Material object pointers; none of
  the 1,024 load-boundary return candidates or 276 resolver return candidates
  equals any of those pointers. This is a same-session exact comparison, not
  address similarity, and it confirms that the observed PPtr/load hooks did
  not produce the Transfer Material object in this run.
- R17: the Shader tree was captured in a single-ClassID 48 experiment. The
  `.hook` includes `TypeTreeRipper.ttbin` (150,991 bytes; 1,548 records),
  with zero transfers and zero explicit identity links. This is valid TypeTree
  evidence, not evidence of a live Shader object identity.
- R15-R17 do not confirm that MuMu was in rendered in-world gameplay. They
  establish runtime extraction/instrumentation behavior only.

#### Hypothesis

- The exact sequential resolver pair may serve object resolution, but current
  evidence does not tell us whether its opaque 16-byte input record encodes a
  serialized-file identity, nor whether its return value is the Material or
  Shader object later transferred/rendered.

#### Blocked / Next Gate

- The original target is only partially feasible so far: field-complete
  Material/Shader TypeTrees are now proven obtainable on HSR 4.5, but the
  identity objective remains unproven. There is still no accepted
  `sourceFile#PathID -> Material/Shader -> RenderDoc EID` chain.
- Do not label `key64` as PathID, `runtimeValue32` as InstanceID, or the R15
  output-slot handoff as a Material/Shader link. Do not use names, address
  similarity, order, ResourceId, or cross-session matching.
- The next experiment must run after the user reaches an in-world rendered
  scene. Pivot from generic PPtr return matching to the actual serialized
  field visitor/Transfer path, using the verified Material TypeTree field
  schema (`Base.m_Shader.m_FileID` and `Base.m_Shader.m_PathID`) as the
  decoder contract. First prove which callback consumes those named fields and
  whether it exposes the owning Material runtime object; then require a direct
  typed-object relation to the observed Material Transfer. For EID, use a
  RenderDoc capture from that same live session and only accept an explicit
  identity producer/consumer chain. If the ABI cannot expose those fields,
  record the exact boundary as blocked rather than widening heuristics.

#### Evidence

- R15 analysis:
  `artifacts/HookSessions/hsr45-pptr-return-sequence-r15-20260917/HSR-4.5-MUMU_20260917_070311745/HSR-4.5-MUMU_20260917_070311745.analysis.json`
  - `summary.directPPtrToResolveChainCount=994`; resolver return caller is
    `0x4000233abd18`; only typed Material candidate is at `0x23a9218`.
- R16 runtime Material tree and report:
  `artifacts/HookSessions/hsr45-native-material-typetree-r16-20260917/HSR-4.5-MUMU_20260917_070947162/runtime/typetree/release.ttbin`
  and `release.report.json`; finalized session `.hook` is adjacent to the
  analysis JSON.
- R17 runtime Shader tree and report:
  `artifacts/HookSessions/hsr45-native-shader-typetree-r17-20260917/HSR-4.5-MUMU_20260917_071637332/runtime/typetree/release.ttbin`
  and `release.report.json`; finalized session `.hook` is adjacent to the
  analysis JSON.
- Corrected source/profile/script:
  `References/TypeTreeRipper/source/android/android_impl.cpp`,
  `profiles/HSR-4.5-MUMU.json`, and
  `tools/Run-HSR45NativeMaterialExperiment.ps1`.

### Follow-up: Active Material Transfer vs Named Field Path, R18

#### Confirmed

- In R3, the `unity.transfer-observer-installed` event for typeId 21 Material
  records original callback address `0x4000234086cc` and the first 32 bytes
  of its ARM64 code. That exact code signature occurs in the installed
  HSR-4.5 `libunity.so` at static VA `0x22086cc`; this ties the observed
  Material vtable callback to a concrete function in the current binary.
- Static ARM64 dataflow at `0x22086cc` loads a 32-bit value from
  `object+0x28` and passes it as the first argument to the Transfer-context
  vtable slot. In R16, `unity.transfer-context-call` records eventSequences
  2227 and 2236 show the same active Material object; `arguments[0]` equals
  the low 32 bits of that object's `+0x28` word, and the callback returns the
  same value. The event's `verifiedRelations` records those exact runtime
  equalities.
- A separate static function beginning at `0x2208798` references the exact
  NUL-delimited string `m_Shader`, calls the serialized-field transfer routine,
  and passes `object+0x28` as its output slot. Nearby field-name references
  include Material-schema names such as `m_CustomRenderQueue`,
  `m_LightmapFlags`, `m_EnableInstancingVariants`, `m_DoubleSidedGI`,
  `disabledShaderPasses`, and `m_SavedProperties`.
- The field-caller observer targeting `0x2208798` was installed, but the
  corresponding execution event count was zero in the R3 session. The active
  Material vtable callback is `0x22086cc`, not `0x2208798`. Therefore the
  named-field path is not yet shown to be the live path that produced the R16
  Transfer-context observations.
- R3's actual `m_FileID`/`m_PathID` events at sequences 75320-75329 have no
  `activeTransferCallChain` or `activeSerializedObjectLoadBoundary`. They
  occur on the same guest thread as a later Material Transfer event, but that
  ordering/thread coincidence does not establish ownership; it is explicitly
  rejected as a Material relation.

#### Observed

- R16 Transfer-context event 2227 is for Material object
  `0x766a41df1240`; `arguments[0]=0x149f6`, result `0x149f6`, and
  `argument0MatchesObjectWord28Low32=true`. This proves the value flow through
  that callback, not the semantic type of `0x149f6`.
- R16 also records the same relation for another Material object at event 2236
  (`object=0x766a43bfabc0`, `arguments[0]=0x149f6`). The repeated integer is
  not an identity join and is not interpreted as PathID, InstanceID, or a
  Shader handle.
- R3 field-transfer records are 83 `m_FileID` and 83 `m_PathID`, all on the
  ARM64 side; zero has a Material Transfer TLS context. The statically
  identified `m_Shader`-string caller candidate at `0x2208798` was not
  observed running; its Material-specific semantic role is unproven.

#### Hypothesis

- The function at `0x2208798` has the appearance of a structured field
  serializer, while the observed runtime Material callback uses a separate
  Transfer-context path. The two paths may share an object layout, but current
  evidence does not prove they operate on the same runtime Material instance
  or that the `+0x28` value is a Shader reference.

#### Blocked / Next Gate

- Do not label the opaque `object+0x28` value or context argument `0x149f6` as
  `m_Shader`, PathID, InstanceID, or Shader identity solely from equal offsets,
  nearby code, or repeated values.
- The next static gate is to identify the actual runtime Transfer-context slot
  target and its semantic consumer, then trace how its first argument is
  resolved. The R16 context-target bytes did not match the retained static
  library at the naively derived file offset, so that RVA must not be guessed.
  Preserve the observed mapping metadata and determine the correct runtime
  image/translation mapping before disassembling it.
- The next runtime gate should remain bounded: only after the context target
  ABI is mapped should instrumentation follow this exact `object+0x28` value
  to a typed Shader object in the same call chain, then to a same-session
  RenderDoc EID. If the value remains opaque, keep the bridge unproven.

#### Evidence

- R3 Material callback installation:
  `artifacts/HookSessions/hsr45-thunk-map-reusevm-r3-20260917/HSR-4.5-MUMU_20260917_031906169/runtime/events.ndjson`
  - eventSequence 71951; typeId 21; original code signature maps exactly to
    `artifacts/analysis/HSR-4.5/guest/libunity-4.5.0-arm64.so` static VA
    `0x22086cc`.
- R3 field visitor records: same `events.ndjson`, eventSequences 75320-75329;
  no active Transfer/load-boundary context. Caller sites include
  `0x239f4c0` and `0x239f58c`.
- R16 Material context relations:
  `artifacts/HookSessions/hsr45-native-material-typetree-r16-20260917/HSR-4.5-MUMU_20260917_070947162/runtime/events.ndjson`
  - eventSequences 2227 and 2236; exact Material object-to-context argument
    equality; opaque value is preserved without semantic relabeling.
- Static string xrefs and disassembly:
  `artifacts/analysis/HSR-4.5/guest/libunity-4.5.0-arm64.so`, functions
  `0x22086cc` and `0x2208798`; the latter's field-caller observer execution
  remains zero in the cited live session.

### Follow-up: Read-Only Material Context Table Probe, R19 (Runtime Complete)

#### Confirmed

- The Transfer-context slot-0 disassembly observed in R16 performs a lower-bound
  lookup over 8-byte entries: compare a 32-bit key, then read the 32-bit value
  at entry offset `+4`; on a miss it returns the input key unchanged. The new
  probe mirrors only this observed access pattern.
- `TypeTreeRipper/source/android/android_impl.cpp` now records, only for the
  already-observed Material context callback, the context table pointer,
  begin/end range, entry count, input key, lower-bound result, optional mapped
  value, original callback result, and whether the table-derived result equals
  that callback result. A matching key, a readable nonmatching lower-bound
  key, and an unreadable lower-bound entry are represented separately; no
  default key value is emitted for an unreadable/missing candidate. It does
  not label any integer as a Shader or asset ID.
- Reads use the existing readable-range guard, validate the range/8-byte
  stride, cap the entry count at `2^24`, and perform at most logarithmic key
  reads. The hook does not alter arguments or return values.
- The updated native code built successfully for Android x86_64 and ARM64; the
  .NET 8 tool build completed with 0 warnings and 0 errors. The HSR 4.5 Profile
  check also passed.
- R19 runtime `release.ttbin` contains one valid Material tree (76 nodes); the
  requested Shader tree is absent. The emitted report marks coverage
  `partial_for_requested_types`, not full Material+Shader coverage.
- R19 analysis contains 26,174 records, 3 observed objects, 2 Transfer
  instances, and 7 Material context calls. The context table lookup was
  complete in all seven records and its derived result matched the original
  callback return in all seven.
- All seven lookups missed: six inputs were `0x149f6` or `0x0` against the
  one-entry table's lower-bound key `0xfffffdbc`; the seventh input was
  `0x1522e` against lower-bound key `0x2d84a`. The callback returned each
  input unchanged. These values remain opaque; this only verifies miss and
  passthrough behavior for these calls.
- The session reports 1,051 exact PPtr-output-slot to resolver-argument
  handoffs, but zero active Material Transfer call chains, zero load-boundary
  PPtr chains, zero typed resolver returns/targets, and zero
  `sourceFile#PathID -> Material/Shader -> RenderDoc EID` bridges.

#### Observed

- R19 was run in session
  `artifacts/HookSessions/hsr45-material-context-table-r19-20260917/HSR-4.5-MUMU_20260917_081207011/`.
- `analysis.json` reports `identityBridge.status=not_observed`,
  `completeMaterialIdentityCount=0`, `completeShaderIdentityCount=0`, and
  `sharedRenderDocEventIdCount=0`. It also records load-boundary and consumer
  observer caps at 16,384 and 8,192 respectively; those saturated streams did
  not create identity evidence.

#### Hypothesis

- The context table is not resolving the sampled nonzero Material values in
  R19; it misses and returns the key unchanged. This narrows the active context
  callback's observed role but does not determine the semantic type of the key.

#### Blocked / Next Gate

- Do not infer the key/value's semantic type from a hit, a miss, or equality.
- Do not continue treating this context table as a Shader resolver. The next
  experiment must capture the actual serialized `m_Shader` PPtr transfer and
  its resolver in one Material Transfer call chain, retaining sourceFile,
  PathID, and the exact runtime object handle. Existing R19 PPtr/resolver
  records are separately valid handoffs but have no such Material call-chain
  link. Only after this identity join is direct may the same-session RenderDoc
  EID be correlated.

### Follow-up: Runtime Context Target vs File-Backed Image, R20

#### Confirmed

- R16 records the context target at `0x400022d839e0` in a mapping with
  `begin=0x4000218f0000`, `fileOffset=0x8ec000`, and
  `offsetWithinMapping=0x14939e0`. The `/proc/self/maps` mapping arithmetic
  gives file offset `0x1d7f9e0` for that runtime address.
- The installed HSR 4.5 `libunity.so` and retained static analysis image have
  the same SHA-256 (`3fd4436ae093c58a958ed78c144f198342b51ade083e7dae9f81d905fb2be68f`).
  Bytes at file offset `0x1d7f9e0` do not match the R16 `targetRawWords`; the
  captured 24-word instruction sequence and its first 4-/8-word prefixes were
  not found elsewhere in that exact file.

#### Hypothesis

- The target's executable view may be transformed, patched, or generated at
  runtime while retaining a file-backed mapping label. The current evidence
  does not distinguish these mechanisms.

#### Blocked / Next Gate

- Do not assign this target a guessed RVA or treat the mapping path as proof
  that its current instruction bytes equal the on-disk image. The table probe
  has verified runtime behavior without resolving code provenance. To follow
  the value beyond passthrough, observe the live `m_Shader` field/PPtr path at
  the typed Material Transfer boundary; the overall identity and RenderDoc
  EID bridge remains unobserved.

### Follow-up: Resolver Entry and PPtr Entry Context Probe, R21-R22

#### Confirmed

- The HSR 4.5 Material TypeTree schema identifies `Base.m_Shader` as
  `PPtr<Shader>` with `m_FileID` (`int`, 4 bytes) and `m_PathID` (`SInt64`,
  8 bytes). This establishes serialized schema only, not runtime values or
  native-memory offsets.
- TypeTreeRipper snapshots the thread-local serialized-field-transfer
  context at entry to `HookedSerializedObjectResolveCandidate`, and the
  sequential PPtr observer snapshots both serialized-field and outer Transfer
  context at PPtr entry. These are read-only same-thread probes; they do not
  change arguments, return values, or identity interpretation.
- Android x86_64 and ARM64 native builds succeeded; the .NET 8 tool build
  completed with 0 warnings and 0 errors, and the runtime libraries were
  deployed to `runtime/HSR-4.5-MUMU`.

#### Observed

- The first R21 attempt started HSR but timed out after 90 seconds waiting for
  a new RTTI catalog. The process was still running at ComboSDKActivity; this
  was an orchestration timeout, not a negative identity result.
- The cause was a control-flow gate: `WaitForAndroidUnityRuntime` ran only
  after foreground recognition succeeded, even when the HSR process was
  confirmed alive. The runner now waits for Unity modules whenever the process
  is alive and the HSR profile requests that wait; its timeout is configurable.
- R21 completed in-world after the user entered the visible title screen. Its
  final stream contained 3,916 records, 274 Transfer callbacks (104 Material,
  158 Shader), 312 serialized-field records (156 `m_FileID` and 156
  `m_PathID`), 194 resolver candidates, and zero `m_Shader` records or active
  serialized-field scopes at resolver entry. It produced no source identity
  or RenderDoc EID bridge.
- R22 added PPtr-entry snapshots and completed its in-world observation. The
  finalized stream contains 9,204 resolver candidates, of which 145 returns
  had an independently observed Material/Shader vtable type (118 Material,
  27 Shader); it also contains 9,059 direct PPtr-to-resolver call-chain records.
  Zero typed candidates carried the new serialized-field or Transfer context
  at PPtr entry, and zero carried a serialized-field scope at resolver entry.
  The configured `m_Shader` filter again matched zero records.
- Exact same-process pointer comparison found 54 overlaps between typed
  resolver returns and Material/Shader Transfer objects. All were on
  different threads, and the closest events were separated by at least
  128,681,214,637 ns (about 128.7 seconds). Reject these as address reuse,
  not object identity. No proximity/order/name/ResourceId rule was used.
- Static ARM64 evidence shows the typed candidate at return RVA `0x23a9218`
  follows a call to `0x23a9a7c`, with arguments forwarded from incoming
  registers (`x19`, `w21`, `w20`). The returned pointer is used as a lookup
  result, but the two 32-bit arguments remain opaque; this does not identify
  a serialized PPtr or source identity.
- The finalized R22 analyzer reports 17,541 records and
  `identityBridge.status=not_observed`; it retained 282 Transfer events and
  the 145 typed resolver returns. The named field records are exactly 156
  `m_FileID` plus 156 `m_PathID`, with no `m_Shader` field event.
- Independent raw-event review found 67 exact same-thread Transfer-context /
  object event pairings, of which 26 satisfy the analyzer's argument/object
  word checks. The analyzer's 146 relation rows reduce to 14 unique
  object/transfer/context tuples; none adds serialized identity or EID.
- R22 contains 276 Transfer snapshots, 8,236 resolver-return snapshots, and
  312 field-output snapshots. The 88 printable strings are not live-value
  evidence; R22's release TypeTree decodes to zero nodes, while its 1,627-node
  baseline is a separate artifact. Its `eventSequence` is not unique: 708
  sequence values repeat (802 extra rows), so cite NDJSON line, kind, and
  thread together.
- R22's installed field-observer event reports module base `0x400021200000`
  and confirms the generic field-transfer target at runtime. Subtracting this
  verified base from the recorded field-event callers maps the observed
  callsites to static RVAs including `0x239f4bc`, `0x239f588`, `0x23a804c`,
  and `0x23a8174`; these contain BL instructions to the observed generic field
  transfer at `0x23c77a4`. The static `m_Shader` call in `0x2208798` would
  return at RVA `0x2208800` (runtime address `0x400023408800` under this
  module base); that caller is absent from all 312 R22 field records. This
  means that specific static path was not observed executing in R22, not that
  every possible Material-field path is absent.

#### Hypothesis

- The tested generic PPtr/resolver callbacks are outside the active field and
  Transfer TLS scopes for this workload. This rejects the tested nesting route,
  but does not prove the actual Material `m_Shader` field visitor is absent or
  unreachable.

#### Blocked / Next Gate

- R22 falsifies the current generic PPtr/resolver context strategy for
  observing the named Material field. Do not repeat it with larger caps or
  loosen joins. The next static/runtime design gate is the executed field
  callers plus the hash-matched recursive path:
  `0x23a95ac -> 0x23bfbe8 -> 0x23a9614 -> 0x23a9118 -> 0x23a9214 ->
  0x23a9a7c`. A bounded observer should capture the recursive resolver's
  incoming opaque 32-bit value and returned typed object in one invocation,
  then test the exact producer-to-consumer register/stack dataflow. Preserve
  the producer's 16-byte input as opaque until it is independently tied to
  named `m_FileID/m_PathID` fields; do not promote any integer to PathID or
  InstanceID. Separately, the code window at `0x2208798` contains an
  `m_Shader` string reference, but its semantic role is unproven and its
  expected callsite was not observed in R22; verify the execution path before
  considering a hook there.
- Only after a direct field-to-typed-Shader relation and sourceFile#PathID
  semantics are proven should work proceed to a same-session RenderDoc EID
  relation. The overall identity bridge remains unobserved.

### Experiment Design Rebuttal and Revised Gate, R23

#### Confirmed

- The proposed chain is not a naturally nested producer/consumer pair. The
  producer call at `0x23a95ac` returns to its caller at `0x23a95b0`; that
  caller stores `w0` to `[x21]` and later, on a branch, passes the value in
  `w1` to the consumer at `0x23a9118` (`BL` at `0x23a9614`, return PC
  `0x23a9618`). A TLS context pushed only inside the producer wrapper and
  popped when it returns would be gone before the consumer executes.
- The `0x23a9118` hook point is a function-entry observation, not the typed
  return point. Its internal resolver call is at `0x23a9214` and returns at
  `0x23a9218`; only that resolver result may be classified by its observed
  vtable, with ambiguity preserved.
- The producer's 16-byte input and the consumer's 32-bit argument remain
  opaque values. Neither has proven PathID, InstanceID, PPtr, or asset identity
  semantics.
- For a hook entered at `0x23bfbe8`, the return address for the known `BL` at
  `0x23a95ac` is `0x23a95b0`. Events must call this `returnPc`, not the BL
  instruction address or a generic `caller`.

#### Revised experiment design

- First perform a no-runtime preflight for the exact parent routine/frame and
  every proposed observer: resolve the RVA against the selected HSR 4.5 ARM64
  ELF, verify module identity/build ID, architecture, full overwritten-code
  window and instruction boundaries, and document each function ABI and
  callsite return PC. Any mismatch means no hook installation.
- Do not model the producer/consumer link as nested TLS. The observation must
  preserve one explicitly scoped parent invocation across the producer's
  return and the later consumer call. Before implementation, prove how that
  scope begins and ends for all returns, branches, recursion, and repeated
  producer calls. A token may be consumed only for the same observed parent
  invocation and exact statically verified return-PC path; thread/time/address
  proximity alone is not a join.
- Record producer input as bounded `opaqueInput16`, producer result as
  `opaque32`, consumer entry `w1` as `opaque32`, consumer raw return, and the
  nested resolver arguments/result/vtable. Record raw module-relative
  `returnPc` and installation status separately from observed-call events.
  Never label the values PathID, InstanceID, PPtr, Material, or Shader merely
  from equality or a vtable class observation.
- Success at this gate is one reproducible, same-parent-invocation record
  showing the statically verified producer result forwarded unchanged into
  the consumer's `w1`, followed by that invocation's resolver result and
  independently supported type classification. This proves only an opaque
  internal dataflow edge.
- This gate does not prove `Material.m_Shader -> Shader`, sourceFile#PathID,
  or RenderDoc EID. Each remains a separate evidence gate. If parent scope or
  the callsite cannot be observed without unsafe/ambiguous ABI assumptions,
  stop and record blocked rather than substituting address/time/name joins.

#### Decision

- The initial experiment wording is **NO-GO** as submitted by the design
  agent. The revised experiment is **conditionally feasible**, but runtime
  execution remains **BLOCKED** until the parent-invocation scope and probe ABI
  are proven from the exact binary and the installation guard covers the full
  displaced instruction window. No code change or MuMu run is authorized by
  the current evidence review.
- Overall end-to-end feasibility remains unproven. Existing evidence supports
  runtime typed Material/Shader observations and matching TypeTree schema, but
  not their direct relation to serialized fields or RenderDoc events.

### Evidence

- R5: `artifacts/HookSessions/hsr45-material-shader-party-switch-r5-20260917/HSR-4.5-MUMU_20260917_044619991/HSR-4.5-MUMU_20260917_044619991.analysis.json`
  - load-boundary/resolve 4096 each; consumer/context 1024 each; all caps
    saturated.
- R6: `artifacts/HookSessions/hsr45-material-shader-party-switch-r6-20260917/HSR-4.5-MUMU_20260917_050217210/HSR-4.5-MUMU_20260917_050217210.analysis.json`
  - load-boundary/resolve 16384 each; consumer 8192; all three capped;
    bridge `not_observed`.
- R7: `artifacts/HookSessions/hsr45-material-shader-party-switch-r7-20260917/HSR-4.5-MUMU_20260917_051252108/HSR-4.5-MUMU_20260917_051252108.analysis.json`
  - 36 typed resolver candidates; 27 Shader pointer intersections; 0 direct
    PPtr chain on typed candidates; bridge `not_observed`.
- R8: `artifacts/HookSessions/hsr45-material-shader-party-switch-r8-20260917/HSR-4.5-MUMU_20260917_052935181/HSR-4.5-MUMU_20260917_052935181.analysis.json`
  - 9,144 resolver records; 131 Material and 34 Shader typed candidate records;
    0 typed direct-PPtr chains; 32 Shader pointer intersections with long
    monotonic gaps; bridge `not_observed`.
- R21: `artifacts/HookSessions/hsr45-material-field-scope-r21-retry-20260917/HSR-4.5-MUMU_20260917_085058861/runtime/.runtime-events.probe`
  - Finalized observation: 3,916 records; no named `m_Shader` field or
    accepted identity relation.
- R22: `artifacts/HookSessions/hsr45-material-field-pptr-context-r22-20260917/HSR-4.5-MUMU_20260917_090851300/HSR-4.5-MUMU_20260917_090851300.hook.analysis.json`
  - Finalized analysis: 17,541 records; 9,204 resolver candidates; 9,059
    direct PPtr call-chain records; 145 typed returns (118 Material,
    27 Shader); 282 Transfer events (124 Material, 158 Shader); 312 field
    events; identity bridge `not_observed`. The 54 exact-pointer
    overlaps are rejected because threads differ and the minimum time delta
    is 128.7 seconds. It also reports 146 Transfer-context relation rows,
    which reduce to 14 unique object/transfer/context tuples.
- R22 raw field events:
  `artifacts/HookSessions/hsr45-material-field-pptr-context-r22-20260917/HSR-4.5-MUMU_20260917_090851300/runtime/events.ndjson`
  - Field-name/caller evidence; installed observer records include the
    version-matched module base and target instruction guard.
- Static caller window: `artifacts/analysis/HSR-4.5/caller-window.txt`
  - RVA `0x23a9214-0x23a9218` call/return and parent call at `0x23a9614`;
    arguments remain semantically opaque.
- R22 executed field-call disassembly: `artifacts/analysis/HSR-4.5/guest/libunity-4.5.0-arm64.so`
  - Capstone/ELF mapping from event callers to the matching on-disk PT_LOAD
    disassembled the actual R22 generic-field caller RVAs listed above. The
    `m_Shader` static return RVA did not occur in the runtime caller set.
- R22 source instrumentation: `References/TypeTreeRipper/source/android/android_impl.cpp`
  - PPtr-entry TLS snapshots were built and deployed before the run; R22's
    zero-context result rejects this tested nesting route.
- The source implementation and the R8 guest binary were built from
  `References/TypeTreeRipper/source/android/android_impl.cpp`; build completed
  for Android x86_64 and ARM64 and .NET 8 with 0 warnings and 0 errors.

## Follow-up: Opaque Producer-to-Resolver Feasibility Reassessment

### Confirmed

- The exact HSR 4.5 ARM64 static chain is bounded to one parent function:
  `0x23a94bc` owns the invocation scope; the producer input/return are observed
  at `0x23a95a8` / `0x23a95b0`; the later consumer argument is at
  `0x23a9610`; the resolver arguments and typed-return observer are associated
  with call/return `0x23a9214` / `0x23a9218`. The original PPtr wrapper already
  provides the same-thread parent invocation token, so no second overlapping
  parent hook is needed.
- R22 already observed 145 typed returns at the exact resolver return RVA
  `0x23a9218` (118 Material, 27 Shader). Thus the resolver path is active in
  the tested HSR 4.5 scene; R23 is specifically intended to add missing
  per-invocation producer/consumer/call-token evidence, not to infer whether
  the function is relevant from a name or a guessed address.
- The code now assigns a unique `invocationId` to each active parent wrapper
  and a separate `resolverCallId` to each observed resolver call. Four
  version-guarded instruction probes record only opaque register/record values;
  the resolver event can join only by active parent token, call token, exact
  arguments, and verified return PC. No value is named PathID, InstanceID, or
  object identity.
- Static ARM64 and x86_64 native builds and the .NET 8 Release build completed
  with zero errors/warnings. The deployed runtime library hashes match
  `runtime/HSR-4.5-MUMU/manifest.json` (`x86_64`:
  `1639A3A418A1E1F37596932DF717F36FCFD8A575C961D6C618B76574BEB3CE08`;
  `ARM64`:
  `EE434AC8347AF7484CC92BA9BB889D0E448816B9B51F244C1269FB2E12798C95`).
- The new audit script parses successfully. Applied to R22, it reports
  `not_installed_or_not_observed`, zero accepted opaque-to-typed links, and
  zero probe scopes. R22 predates this probe set and is not a runtime test of
  the new binary.
- `tools/Test-HSR45OpaqueCallsiteEvidence.ps1` passes four audit regressions:
  one valid same-invocation link is accepted exactly once; a mismatched
  `resolverCallId`, multiple typed candidates for one resolver call, and a
  failed instruction-window guard are rejected. These synthetic cases test the
  audit logic only, not runtime behavior.

### Feasibility Decision

- **Next bounded milestone: feasible to test.** Static caller/register flow,
  existing parent invocation wrapper, version-specific instruction guards,
  and a strict same-call audit now define a reproducible experiment. It can
  establish only `opaque producer result -> consumer argument -> typed
  resolver return` inside one parent invocation.
- **Final goal: still unproven.** Even a successful next milestone does not
  give the opaque input semantic `sourceFile#PathID`, does not prove a
  `Material.m_Shader` field-to-Shader object relation, and does not produce a
  RenderDoc EID join. Each requires separate direct evidence. Overall success
  therefore remains conditional, not guaranteed; a failed semantic-field
  cross-check would require pivoting away from this internal-key path.

### Runtime Gate

- The running MuMu process was left untouched. Updating the on-disk runtime
  library cannot change code already loaded in the game process; validation
  must occur at the next controlled HSR process start. R22 is retained as a
  negative control only.
- Accept the experiment only if a fresh event file contains all four probe
  installation guards passing and, within one exact capture session/process/
  thread/parent invocation, one input, producer return, consumer argument, and
  resolver call token. The producer result must equal the consumer `w1`; the
  resolver event must carry the same call token/arguments and return through
  `0x23a9218` with independent Material/Shader type evidence. Otherwise report
  which edge was absent; never fall back to address, sequence, name, or timing.

### Evidence

- `artifacts/analysis/HSR-4.5/caller-window.txt`, lines 135-136 and 362-391:
  caller/return windows and dataflow.
- `References/TypeTreeRipper/source/android/android_impl.cpp`,
  `InstallHsr45OpaqueCallsiteObserver`, probe callbacks, and
  `HookedSerializedObjectResolveCandidate`: invocation/call tokens, instruction
  guards, and typed resolver correlation.
- `tools/Audit-HSR45OpaqueCallsiteEvidence.ps1`:
  strict event-level acceptance gate; R22 audit output is
  `artifacts/analysis/HSR-4.5/r22-opaque-callsite-audit.json`.
- `tools/Test-HSR45OpaqueCallsiteEvidence.ps1`: synthetic positive and
  negative audit regression tests; not runtime evidence.
- `runtime/HSR-4.5-MUMU/manifest.json`: deployed binary hashes and profile.
