# HSR 4.5 Direct Material Callsite and Probe Rate Limit (R87-R88)

## Scope

HSR 4.5.0 / Unity 2019.4.34f1 on MuMu. R87 and R88 observe the ARM64 guest direct call at `libunity.so+0x23a9210`, whose statically checked `BL` at `+0x23a9214` targets `+0x23a9a7c`. The PPtr scope observer was disabled. The experiment does not label opaque arguments as PathID, FileID, or instance IDs.

## Confirmed

- R87 finalized successfully with 1,527 valid runtime records and a 7,755-byte RTTI catalog. The ARM64 observer install passed the static caller and callee instruction guards.
- R87 logged 256 unscoped raw callsite rows, then the probe returned before recording any more thread-local call tokens. The resolver observer nevertheless saw 76 returns whose runtime vtable was typed as Material. The first such return is event sequence 1111, NDJSON line 1222, resolver call index 51,773. It has no `sameRawResolverCallsiteInvocation`, so it is not accepted as a callsite-to-return link.
- R87 produced 302 serialized-field-transfer rows: 151 `m_FileID`, 151 `m_PathID`, zero `m_Shader`; it produced 98 Material Transfer rows. These observations provide no source-file/PathID owner relation.
- The code was changed so unscoped raw event output remains capped at 256, while the thread-local callsite token is still set for later calls. Resolver-return output is kept for typed targets rather than logging every non-target call. The direct-return audit accepts a missing raw row only when the candidate itself contains the exact same-thread token, exact argument-match flag, callsite/callee static guard metadata, and an explicit `rawProbeEventEmitted=false`; contradictory or ambiguous evidence remains rejected.
- Offline positive and negative audit fixtures pass, including an omitted-raw-row positive case and a contradictory-raw-row rejection case. `git diff --check` passes.
- R88 rebuilt and deployed the x86_64 and ARM64 guest libraries and the .NET 8 host tool. The finalized R88 audit accepts 154 direct callsite-to-typed-Material returns; all 154 are explicitly marked `rawProbeEventEmitted=false`. First accepted candidate: event sequence 855, NDJSON line 966, resolver call index 51,570, thread 6325. Its two arguments are preserved as opaque 32-bit values and its returned object is typed Material from the resolver hook's runtime vtable observation.
- R88 finalized successfully with 1,355 valid runtime records and a 4,684-byte RTTI catalog. MuMu remained booted and the HSR process reached the Administrative District scene.
- R88 still produced 302 serialized-field-transfer rows: 151 `m_FileID`, 151 `m_PathID`, zero `m_Shader`; it produced 98 Material Transfer rows. There are no `sourceFile#PathID` or RenderDoc EID events in this run.

## Observed, Not Promoted

- Repeated resolver returns sometimes have the same returned-object pointer during one capture. This is retained as raw runtime observation only; no address reuse or serialized-identity assumption is made.
- The opaque resolver argument values are not decoded. In particular, neither the 32-bit key nor its second argument is called PathID, FileID, or an instance identifier.
- The exact callsite-to-Material return proves a typed return on that direct call path; it does not prove the serialized asset that produced it, a Material `m_Shader` field, a Shader object edge, or a RenderDoc EID.

## Blocked / Next Gate

- No Material `m_Shader` value was observed, even though Material and Shader TypeTrees are available from prior version-matched runtime captures.
- No same-call-chain `sourceFile#PathID -> Material/Shader` relation and no same-session Material/Shader-to-RenderDoc-EID relation exists.
- Next, trace the actual live Material-to-Shader load/read path from the returned typed Material object. Require a same-thread invocation token that binds the typed Material owner, the decoded `m_Shader` PPtr field and value, and the typed Shader return. Do not infer serialized identity from the opaque resolver arguments or pointer reuse. Only after that join exists should a synchronized RenderDoc capture be added.

## Evidence Files

- `artifacts/HookSessions/hsr45-r87-direct-material-field-transfer-20260918/HSR-4.5-MUMU_20260918_021051532/runtime/events.ndjson` — R87 event sequence 1111 / line 1222; 256 raw callsite rows, typed returns without direct tokens, and the field/Transfer counts above. SHA-256 `C36E49EE8F9577FCFEABAF6AC4B55BBB646D309040D09A8E89D158FA491DEF10`.
- `artifacts/HookSessions/hsr45-r88-material-return-after-cap-20260918/HSR-4.5-MUMU_20260918_023145730/runtime/events.ndjson` — R88 event sequence 855 / line 966; exact token plus typed Material return; no `m_Shader` field event. SHA-256 `55F5FC7A9FD5E035D433D27EB33DCDA5DE76516CDBB703DA06E76DF688C382C6`.
- `artifacts/HookSessions/hsr45-r88-material-return-after-cap-20260918/HSR-4.5-MUMU_20260918_023145730/opaque-callsite-audit.json` — 154 verified callsite-to-typed-return links; `endToEndIdentityBridge=not_proven`.
- `tools/Test-HSR45OpaqueCallsiteEvidence.ps1` — strict positive/negative fixtures for logged and rate-limited raw callsite rows.
- `source/android/android_impl.cpp` in the TypeTreeRipper checkout — token lifetime, raw-row cap, and typed-return filtering implementation.
