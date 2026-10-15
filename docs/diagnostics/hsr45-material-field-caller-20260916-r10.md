# HSR 4.5 Material Field Caller Experiment r10

## Scope

This run repeats the HSR 4.5 MuMu observer on a verified character-detail
route and additionally opens the Skills tab. It does not infer identity from
names, addresses, ordering, key64, runtimeValue32, or RenderDoc ResourceId.

## Runtime evidence

- Session: `HSR-4.5-MUMU_20260916_155012107`.
- Final UI evidence: `artifacts/hsr45-r10-screen-06.png` shows the character
  detail Skills page after the game scene was reached.
- Relevant process: `2360`.
- Selected caller RVA: `0x2208798`, expected first instruction `0xd10343ff`.

## Results

- Material Transfer events: `146`.
- Filtered serialized-field Transfer events: `344` total.
- `m_FileID`: `172`; `m_PathID`: `172`; `m_Shader`: `0`.
- Selected caller execution: `0`.
- PPtr candidate events: `65536` (observer cap reached).
- Explicit Material-to-Shader or Material-to-RenderDoc-EID relation: `0`.

## Classification

### Confirmed

- The 4.5 runtime was running through the character detail and Skills UI,
  so this is not a login-only or scene-entry failure.
- Real `Material` Transfer records were observed in the same process.
- The filtered field observer captured only `m_FileID/m_PathID` for this route;
  it did not observe `m_Shader`.

### Observed

- The statically selected `m_Shader` caller installed in the runtime but did
  not execute during either the character-detail or Skills-page window.
- The observed Material Transfer records and filtered field records do not
  carry a shared same-call object token in this run.

### Hypothesis

- The selected caller belongs to another serialized loading path, or the
  Material shader reference is resolved through a different entry point than
  the observed generic PPtr/field path.

### Blocked

- No Material `m_Shader` value is accepted as decoded runtime data.
- No `sourceFile#PathID` or RenderDoc EID bridge is accepted.
- More UI navigation is not a useful next experiment. The next step is static
  call-chain analysis of the observed Material Transfer and its field-reader
  callsites, followed by one bounded object-token experiment.

