# HSR 4.5 End-to-End Feasibility Reassessment, 2026-09-17

## Decision

The goal is **technically plausible but not yet demonstrated end to end**. The
current evidence does not justify promising that the full chain will succeed.
The work is still worth continuing because each unresolved edge can be tested
independently and rejected without semantic guessing.

The target chain remains:

`serialized sourceFile#PathID -> runtime Material -> its Shader -> exact draw -> RenderDoc EID`

## What Is Established

- The matching HSR 4.5 / Unity 2019.4.34f1 Material schema contains
  `m_Shader.m_FileID` and `m_Shader.m_PathID`; this establishes available
  serialized fields, not a runtime object join.
- Prior runtime sessions observed typed Material and Shader Transfer callbacks.
  R22 also observed 145 typed resolver returns at RVA `0x23a9218` (118
  Material and 27 Shader), but did not bind those returns to serialized fields.
- RenderDoc replay obtains an action's EID by looking up its current chunk offset
  in `m_ActionUses` inside `HandlePreCallback`. This is an explicit
  chunk-offset-to-EID mapping, not a ResourceId or draw-order guess.
- Vulkan debug-label chunks are serialized and replayed as `SetMarker` actions
  with their label text. A unique token can therefore be transported through a
  capture, provided the guest actually emits it and the capture preserves it.

## What Is Not Established

- R22's runtime TypeTree output decoded to zero nodes. The historical non-empty
  schema artifact is separate and must not be presented as R22 runtime field
  data.
- R22 recorded no Draw/EID events. Its pointer intersections were rejected by
  process/thread/time and type-context checks.
- The RenderDoc working-tree sidecar currently records replay chunk offset,
  EID, command-buffer ResourceId, and action flags only. It does not receive a
  verified HSR object token.
- A debug label adjacent to a draw is not, by itself, proof that the label's
  object is the Material used by that draw. A valid bridge needs an explicit
  token emitted from a verified Unity object-to-draw call path, scoped to the
  exact command buffer/draw and rejected on duplicate, missing, or ambiguous
  consumption.
- No runtime observation currently proves `Material.m_Shader` points to the
  observed typed Shader object, nor that either object's `sourceFile#PathID`
  is known.

## Feasibility By Edge

| Edge | Assessment | Evidence gate |
| --- | --- | --- |
| TypeTree schema fields | Feasible; historical schema exists | Reproduce non-empty field-level runtime output for the target session, or explicitly use the version-matched schema as static metadata only |
| serialized identity to typed object | Unproven; bounded experiment is testable | One exact invocation must carry the serialized field inputs through the verified resolver to a typed Material/Shader result; opaque values stay opaque |
| Material to Shader object | Unproven; likely requires a direct PPtr resolution observation | Match the Material's actual `m_Shader` FileID/PathID read and the returned Shader object in the same object/load chain |
| draw to RenderDoc EID | RenderDoc side is feasible | Preserve a token emitted at the verified Unity draw association point and map its captured draw chunk offset to EID; reject any token not consumed by exactly one draw |
| complete chain | Conditional, not confirmed | All edges above must close with independent evidence in a reproducible HSR 4.5 session |

## Next Execution Gate

R23 is a narrow experiment, not a general dump. Use the already-built,
version-guarded probes only in a fresh HSR process. Accept its result only if
all install guards pass and one exact parent invocation contains the producer
input, producer result, exact forwarded consumer argument, and the same
resolver-call token with independently verified Material/Shader type. It may
establish only an opaque dataflow edge; it cannot rename the opaque values as
PathID or InstanceID.

The current MuMu process must not be interrupted to run this gate. After an
authorized fresh HSR process produces a valid R23 record, the next experiment
must target the semantic `m_FileID`/`m_PathID` read and typed object resolution
in one call chain. Only after that is established should a capture-time token
be prototyped. Do not change RenderDoc or add a label-to-next-draw heuristic
before identifying a verified Unity object-to-draw instrumentation point.

## Evidence

- `evidence-ledger.json`, `latestR22ThreeAuditReview`: resource, static/ABI,
  and runtime classifications; R22 had zero Draw/EID events and no accepted
  identity bridge.
- `evidence-ledger.json`, `latestR23FeasibilityReassessment`: guarded probe
  design/build evidence and the R22 negative-control result.
- `artifacts/analysis/HSR-4.5/r22-opaque-callsite-audit.json`: zero accepted
  links from the pre-probe R22 session; not a runtime test of R23 binaries.
- `D:/Unpack_Workspace/Base_RenderDoc/my-renderdoc/renderdoc/driver/vulkan/vk_core.cpp`,
  `HandlePreCallback`: EID lookup by `m_CurChunkOffset`; local sidecar is
  replay-only.
- `D:/Unpack_Workspace/Base_RenderDoc/my-renderdoc/renderdoc/driver/vulkan/wrappers/vk_cmd_funcs.cpp`,
  `Serialise_vkCmdInsertDebugUtilsLabelEXT`: replay retains labels as marker
  actions; this does not establish a game-object association.

