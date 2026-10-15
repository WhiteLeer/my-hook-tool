# HSR 4.5 Material Field Transfer R3

## Scope

This run validates the ABI-clean `void` observer for the statically identified HSR 4.5 ARM64 field-transfer function at RVA `0x23c77a4`. The observer was exercised on the title flow, administrative district, team view, and character detail view.

## Confirmed

- The target instruction at `0x23c77a4` matched `0xf81a0ffe` and the `void` wrapper installed in the ARM64 guest path.
- The process completed without a guest crash.
- The event stream contains 4,096 bounded `unity.serialized-field-transfer` records.
- It contains 85 `m_FileID` records and 85 `m_PathID` records, with bounded output snapshots.
- The wrapper now uses a dedicated `void (*)(void*, const char*, void*, void*, uint32_t)` ABI and no longer interprets the target's return register as a value.

## Observed

- The captured stream has 31 stream identities and repeated callers for `m_FileID` and `m_PathID`.
- It includes fields such as `sdfTexture`, `m_Material`, `Base`, and curve/vector fields.
- It contains no `m_Shader` field record in the bounded window.
- The generated `.hook` still reports zero accepted AS Material/Shader bindings and zero explicit identity associations.

## Not proven

- `argument2` is not treated as a field value.
- `outputSlot` snapshots are not decoded as `sourceFile#PathID`.
- Repeated stream/caller/output patterns are not treated as object identity.
- No Material-to-Shader or Material/Shader-to-RenderDoc EID bridge is claimed.

## Next gate

Trigger a transition that is proven to reach the statically verified `m_Shader` caller at RVA `0x2208798`, then capture the complete same-thread call window around that invocation. Acceptance requires a direct same-object relation between the field transfer, the Material/Shader transfer, and the RenderDoc event; names, addresses, order, and ResourceId remain insufficient.
