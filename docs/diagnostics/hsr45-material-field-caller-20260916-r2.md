# HSR 4.5 Material Field Caller Experiment r2

## Scope

This run tests one statically verified ARM64 caller of the observed serialized
field Transfer target. It does not decode arguments or assign PathID,
InstanceID, object ownership, or shader semantics.

## Static evidence

- Target Transfer RVA: `0x23c77a4`.
- Caller RVA: `0x23a7fdc`.
- Caller first instruction: `0xd10103ff` (`sub sp, sp, #64`).
- The caller contains direct `bl` calls to `0x23c77a4` at `0x23a8018`,
  `0x23a804c`, and `0x23a8174`.
- The caller was installed only after the runtime value at the configured RVA
  matched the expected first instruction.

## Run

- Session: `HSR-4.5-MUMU_20260916_141727889`.
- Events: `79116`.
- Caller observer installation: `status=installed`, `dobbyStatus=0`.
- Caller observations: `5`.
- Serialized field Transfer observations: `3771`.
- Transfer events with `activeSerializedFieldCaller`: `15`.
- In all 15 nested records, caller raw `arg2` equals the Transfer `stream`
  value by exact JSON value comparison; this is a direct argument match, not
  an object-identity interpretation.
- Guest crash: none.

## Classification

### Confirmed

- The configured caller hook installed at the expected runtime address.
- The hooked caller executed on the guest.
- Transfer callbacks were observed nested inside that caller on the same thread;
  the event records preserve the caller raw arguments and the Transfer caller.
- This closes a direct same-thread call-chain relation between the selected
  static caller and selected Transfer target.

### Observed

- Nested examples include `m_Script`, `m_FileID`, and `m_PathID` Transfer
  records. The nested caller context contains raw `arg0` through `arg4`, but
  no field semantics are assigned.
- The exact `arg2 == stream` match is present in all 15 nested records. It
  confirms argument flow from the selected caller into Transfer, but does not
  identify the stream as a Material, Shader, PathID, or InstanceID.
- The runtime event stream still has no accepted Material object identity,
  Shader object identity, sourceFile#PathID value, or RenderDoc EID.

### Hypothesis

- The selected caller is a real serialized field-loading path used by the
  current HSR 4.5 startup/content path. Its raw arguments may provide the
  next boundary for identifying the owning serialized object, but this must be
  proven by a second same-object relation rather than inferred from addresses.

### Blocked

- It is not yet valid to interpret `arg0`/`arg1`/`arg2`/`arg3`, output slots,
  `key64`, or event order as Unity identity.
- Material-to-Shader and Material/Shader-to-RenderDoc-EID mapping remain
  unproven.

## Next gate

Add one bounded, read-only observation at the caller's owning-object boundary
and require the same runtime object token to be present in both that boundary
and the nested Transfer record. Only after that match may a field value decoder
be considered.
