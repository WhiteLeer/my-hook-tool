# HSR 4.5 Field Read Observer Result

## Confirmed

- The HSR 4.5 static caller window at `libunity.so` RVA `0x23a7e34`
  prepares five arguments and calls RVA `0x23c87b8`: stream/object in `x0`,
  two string arguments in `x1`/`x2`, an output slot in `x3`, and `1` in `w4`.
- The runtime installation record for RVA `0x23c87b8` matched the expected
  first instruction `0xd103c3ff` and reported `dobbyStatus=0` on the ARM64
  guest companion.
- The bounded HSR 4.5 run completed without a guest crash and produced 79,406
  valid runtime events.

## Observed

- The run emitted three field-read observer installation records because the
  loader retries installation across processes/architectures; only the ARM64
  record is relevant to the guest call path and it reports `status=installed`.
- The run emitted zero `unity.serialized-field-read` records. It therefore did
  not execute the target function during the observed startup/login window.
- Existing PPtr and object-load observers remained active, but no direct field
  value was produced and no identity bridge was created.

## Interpretation

The candidate's ABI is supported by the static caller shape and the guest
first-instruction check, but invocation is not yet observed. This is an
instrumentation coverage result, not a successful or failed field decoder.

## Next Gate

Repeat the same binary with the game already inside a scene that loads a
Material, or use an equivalent controlled scene transition. Keep the observer
set and RVA unchanged. Accept the field path only when a real event contains
the field name, output snapshot, caller, and an active Transfer or serialized
object-load call chain in the same thread.

## Scene Run Follow-up

- A second run entered the HSR 4.5 game flow and emitted 16 `Material`
  `unity.transfer` records and 54 `unity.transfer-context-call` records.
- The same run still emitted zero `unity.serialized-field-read` records.
- Therefore the absence is not explained by the absence of Material loading.
  The candidate is installed and statically plausible, but it is not the
  observed field-access path for these Material transfers, or the actual field
  access uses a different ABI boundary.
- The next step is static caller tracing from the observed Transfer-context
  calls. The existing field-read RVA must not be treated as the Material field
  decoder until a live invocation is observed.
