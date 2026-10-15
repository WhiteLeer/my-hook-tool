# HSR 4.5 Runtime Probe

## Scope

This record is for the installed HSR package only:

- Profile: `HSR-4.5-MUMU`
- Package: `com.miHoYo.hkrpg`
- Client version observed: `4.5.0`
- Unity revision: `2019.4.34f1`
- Guest ABI: `x86_64` Native Bridge

No HSR 4.4 client was installed or launched for this run.

## Verified Result

The safe `--vtable-probe 1` run completed with the rebuilt guest library.
The Ripper log shows the structured RTTI fallback, object processing, and two
`Vtable probe complete; skipping native TypeTree call` entries. The session
also stores the bounded Ripper logcat at
`runtime/typetree/ripper-logcat.txt` and records its SHA-256 in `.hook`.

The session produced:

- runtime RTTI catalog: 182 structured types, 4,684 bytes;
- runtime TypeTree artifact: structurally valid, 89 bytes, 0 field nodes;
- runtime transfer events: 0, because this was a vtable-only probe;
- Shader/Material bridge records: 0.

## Boundary

The probe proves that the current 4.5 process can load the Ripper and expose
object vtable evidence. It does not prove a TypeTree ABI. The current 4.5
transfer memory does not pass the complete node-array and string-buffer
validation, so the tool must not decode it as fields. The old 4.4 slot or
transfer layout is not used as a fallback.

The next valid input is a 4.5 scene-level transfer observation. A complete
field tree still requires a target-specific function/layout proof; the native
TypeTree call remains disabled unless the caller explicitly supplies the
existing safety confirmation and a target-specific slot.
