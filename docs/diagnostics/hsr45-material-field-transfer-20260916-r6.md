# HSR 4.5 Material Field Transfer R6

## Scope

The ABI-clean observer was run through the HSR 4.5 administrative district, team view, and character detail view. The character detail view was reached before the event window completed.

## Result

- 4,096 bounded `unity.serialized-field-transfer` records were emitted.
- The stream contained 85 `m_FileID` records and 85 `m_PathID` records.
- No `m_Shader` field record was emitted.
- No field-transfer record carried an active same-call `activeTransferCallChain` or `activeSerializedObjectLoadBoundary`.
- The process completed without a guest crash, but the generated hook contained zero accepted AS Material/Shader bindings and zero explicit identity associations.

## Interpretation

The character page provides real Material/Shader-heavy coverage, so the missing `m_Shader` record is no longer adequately explained by simply being on the wrong screen. The current field-transfer target is either not the runtime path used for the Material's shader reference, or the shader reference is passed through another object-loading path. The field names and bounded snapshots remain opaque and are not decoded as identities.

## Next gate

Use the statically verified `m_Shader` string cross-reference at RVA `0x2208798` to instrument its caller or its object-loading predecessor. The experiment must first prove that this call site executes in HSR 4.5, then capture its receiver/argument and the immediate field-transfer call in one call window. Do not infer a Material/Shader identity from temporal proximity, addresses, field names, or RenderDoc ResourceId.
