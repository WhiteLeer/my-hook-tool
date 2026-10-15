# HSR 4.5 identity writeback site runtime evidence

## Scope

This is a runtime observation of the HSR 4.5 client in MuMu. It does not patch
the caller-side writeback instruction. The scanner only records a site when an
ARM64 `BL` resolves to the verified serialized-identity target and the next
instruction is the exact observed `str w0, [x28, #0x10]` encoding.

## Reproduction

- Session: `HSR-4.5-MUMU_20260911_071803983`
- Hook artifact: `artifacts/HookSessions/identity-writeback-site-runtime-20260911-r2/`
- Guest ABI: `x86_64`
- Guest package version: `4.5.0`
- TypeTree observer: enabled
- Native TypeTree ABI experiment: disabled

The event file contained 2708 valid JSON Lines and no malformed records. It
reported 1724 serialized-identity observations, 519 delayed writeback probes,
and 4 site records. The site scan was executed twice during guest library
initialization and each pass found the same two sites:

| call PC | identity target | store PC | evidence |
| --- | --- | --- | --- |
| `0x4000233ad174` | `0x4000233befb0` | `0x4000233ad17c` | direct BL + `cmp w21, w27` + exact store |
| `0x4000233ad194` | `0x4000233befb0` | `0x4000233ad19c` | direct BL + `cmp w21, w27` + exact store |

The exact observed middle instruction is `0x6b1b02bf` (`cmp w21, w27`),
and the store instruction is `0xb9001380`. Both were read successfully in a
second runtime session and matched on both sites.

A subsequent read-only session recorded the instruction at `callPc + 0x0c`:
`0x54000161` and `0x54000061`. These are conditional PC-relative branches.
The optional caller patch therefore rejects both sites instead of relocating
them blindly. An earlier prototype that did not make this check produced no
identity calls; it is not considered valid evidence.

A guarded `B.cond` relocation prototype was then tested. It preserved the
original compare and store, used an inverse-condition branch plus an absolute
jump for the original branch target, and restored the caller frame before both
exit paths. The process remained alive, but the session produced zero
identity calls and zero post-store records. This run is therefore invalid as
identity evidence and the prototype must remain opt-in.

## What this proves

- The caller-side writeback site is dynamically present at the same addresses
  already found by the static baseline.
- The writeback occurs after the serialized-identity function returns; the
  previous callee hook therefore cannot read the final value at return time.
- The `.hook` and event parser can consume the new evidence without JSON
  corruption.

## What this does not prove

The current data does not prove that `key64`, `runtimeValue32`, or the value
stored at `x28 + 0x10` is Unity `sourceFile#PathID`. It also does not establish
a Material/Shader or RenderDoc EID relation. Names, addresses, ordering,
`ResourceId`, and hash equality must not be used as substitutes for that
missing same-object evidence.

## Fresh identity and transfer run

Session `identity-transfer-fresh-20260911` enabled the read-only identity
observer and the existing Transfer observer in the same HSR 4.5 launch. It
produced 1,724 identity records and 21,434 Transfer records. Every identity
record had readable `keyBytes16`; the observed values were:

- `keyWord0`: one distinct value, `1353`.
- `key64`: 1,724 distinct values.
- `runtimeValue32`: 1,724 distinct values.
- caller: `0x4000233ad198` for 1,723 records and `0x4000233ad178` for 1 record.

The 16-byte key shape and uniqueness are useful runtime evidence, but they do
not identify the fields' Unity meanings. In particular, `keyWord0` is not
renamed to `sourceFile` and `key64` is not renamed to `PathID` without a
same-object serialized reference proof. The same run produced a 31-byte empty
`release.ttbin`, because safe Transfer observation does not invoke the
unverified native TypeTree ABI; the known 211,883-byte non-empty tree remains
limited to the explicitly guarded native experiment.

## Next experiment

The next isolated experiment must use debugger-level single-step or breakpoint
observation, or first decode the complete caller control flow before another
relocation attempt. It must observe the same caller frame after the verified
store while preserving the original instructions and recording the post-store
value together with the same call context. It should remain opt-in and
separately guarded from the current read-only scanner.
