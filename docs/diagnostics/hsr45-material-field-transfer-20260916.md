# HSR 4.5 Material field-transfer observer

## Static basis

The HSR 4.5 ARM64 `libunity.so` function at RVA `0x2208798` passes the
literal field name `m_Shader` to RVA `0x23c77a4`. The target consumes five
arguments and returns through a normal void-style epilogue. This is a direct
caller/data-flow basis for observing serialized field transfer; it is separate
from the earlier `0x23c87b8` candidate.

Evidence:

- `artifacts/analysis/HSR-4.5/libunity.so`
- `artifacts/analysis/HSR-4.5/identity-consumer-window.txt`
- target first instruction at `0x23c77a4`: `0xf81a0ffe`
- caller field literal at `0x3e0cc1`: `m_Shader`

## Confirmed

- The new read-only observer at `0x23c77a4` installed and the HSR 4.5 scene
  completed without a guest crash.
- The observer emitted 316 `unity.serialized-field-transfer` events.
- The records contain exact field names including `m_FileID`, `m_PathID`,
  `m_Material`, and many Particle/Shader/Material field names from live calls.
  The `m_Shader` name is currently confirmed only by the static caller literal,
  not by this run's live field stream.
- The old field-read observer remains available and was not changed to use the
  new target by default; the new path is enabled explicitly with
  `-EnableMaterialFieldTransferObserver`.

## Observed

- In the bounded scene run, the live field-transfer stream contained 85
  `m_FileID` records and 85 `m_PathID` records, but no live `m_Shader` record.
- Many field calls had an opaque `argument2`, output slot, flags, and caller PC;
  those values were recorded without assigning serialized identity semantics.
- The field-transfer callbacks did not carry the same active Transfer object
  relation as the later `unity.transfer-context-call` observer.

## Hypothesis

- The missing `m_Shader` event is coverage, not evidence that the field does not
  exist: the selected scene reached other serialized objects and did not enter
  the Material field path that contains the static `m_Shader` caller.

## Blocked

- `m_FileID`/`m_PathID` field names alone do not prove their values, object
  ownership, or a `sourceFile#PathID` identity.
- No live `m_Shader` field event has yet been joined to a Material Transfer,
  Shader Transfer, or RenderDoc EID in the same call chain.
- The output-slot snapshots for this run are incomplete for several primitive
  field calls, so no field value decoder is accepted yet.

## Next gate

Repeat the same observer with a scene or asset transition that statically and
visibly reaches the Material caller containing `m_Shader`. Accept a Material
link only when one event contains the exact field name, a valid output value,
the owning serialized object/stream, and a later same-object Transfer relation.
