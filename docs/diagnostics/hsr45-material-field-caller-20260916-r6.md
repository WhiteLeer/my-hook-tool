# HSR 4.5 Material Field Caller Experiment r6

## Scope

This run validates the caller observer in an actual HSR 4.5 gameplay scene.
It does not infer object identity from addresses, ordering, keys, or ResourceId.

## Runtime conditions

- Session: `HSR-4.5-MUMU_20260916_145714802`.
- The app was restarted by the experiment runner.
- The login screen was observed, entered once, and the final screenshot showed
  the administrative district gameplay scene.
- Caller RVA: `0x2208798`.
- Expected first instruction: `0xd10343ff`.
- The generated guest config contained the caller observer settings.

## Results

- Caller observer installation: 1, `status=installed`, `dobbyStatus=0`.
- Relevant process: `2346`.
- Runtime events for that process: `80445`.
- Serialized field Transfer events: `4096` (observer cap reached).
- Caller execution events for `0x2208798`: `0`.
- Nested caller-to-Transfer events: `0`.
- `m_Shader` field events: `0`.
- PPtr events in this process: `0`.

## Classification

### Confirmed

- The rebuilt host writes the caller observer configuration into the guest
  config and the runtime installs the hook at the expected address.
- The experiment reached a real HSR 4.5 gameplay scene after login.
- The selected caller was not executed during this scene's observed window.

### Observed

- Transfer traffic continued in the scene, but it was not nested below the
  selected `m_Shader` caller.
- The captured Transfer records do not contain an accepted Material-to-Shader
  identity relation.

### Hypothesis

- `0x2208798` is a valid serialized caller for a different content-loading
  path, but this scene did not load the Material field through that path during
  the observation window.

### Blocked

- No Material-to-Shader identity bridge was produced by this run.
- No sourceFile#PathID decoder or RenderDoc EID bridge is accepted.
- The next experiment must deliberately load content known to exercise the
  selected caller, or identify another statically verified caller for the
  observed Transfer path. It must not promote generic Transfer records to
  Material records.

