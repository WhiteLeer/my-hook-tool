# HSR 4.5 Runtime Coverage

## Scope

These runs use the currently installed HSR 4.5.0 Android package through MuMu.
No HSR 4.4 client was launched, and the native TypeTree call remained disabled.

## Evidence

- `HSR-4.5-foreground-detect-20260903` detected
  `topResumedActivity=com.miHoYo.hkrpg/com.mihoyo.combosdk.ComboSDKActivity`.
- The session produced one observer-install record, zero real transfer callbacks,
  `startup-only` coverage, and zero TypeTree field nodes.
- `HSR-4.5-typetree-a8-scene-20260903` installed the `+0xa8` observer for 14
  selected Unity types, but also received zero transfer callbacks. Its earlier
  `scene-observed` label was caused by counting observer-install records as
  scene records; the collector now excludes those records.
- Static ARM64 analysis of the Material `+0xa8` target shows calls into the
  TypeTree-node builder and access to the transfer's node iterator fields. This
  is ABI evidence for a candidate generation slot, not proof that the slot is
  called during normal gameplay.
- A cold-start run with the broad observer list crashed while constructing
  `Camera` under `kCreateObjectDefaultNoLock`. The collector now keeps Unity's
  default factory synchronization and the 4.5 profile uses a minimal, verified
  `Material` observer list until other ClassIDs pass isolated startup tests.

## Boundary

The safe observer can prove package version, RTTI catalog coverage, foreground
state, and real transfer callbacks. It cannot produce a non-empty field-level
TypeTree until a verified call path invokes the target-specific generator. The
current next blocker is therefore the invocation path, not login detection or
the MUMU drive letter. The native invocation remains explicitly gated because
an incorrect ABI can crash Unity or corrupt the capture.

The HSR 4.5 profile now keeps the runtime event observer alive for up to 1800
seconds. Manual account authentication is still required; reaching a game
scene is what changes coverage from startup-only to scene-observed.

## 2026-09-03 isolated asset observation

- `HSR-4.5-MUMU_20260903_071443236` was run against package `4.5.0` after
  entering the already authenticated account. The captured guest screen shows
  the Administrative District scene, not the login page.
- The isolated class set was `Texture2D(28), Shader(48), Material(21)`. The
  stream contains `870` Texture2D, `562` Shader, and `477` Material transfer
  callbacks, plus six bounded layout-evidence records.
- No callback contains a verified Unity asset path, PathID, field list, or
  Shader-to-Material binding. The records contain object/vtable identity and,
  when explicitly enabled, raw Transfer words only.
- The collector's scene predicate no longer treats Shader, Material,
  Texture2D, AnimationClip, or Camera alone as proof of a loaded game scene;
  this prevents the startup cinematic from ending the observation window before
  the user presses the login/enter button.

## 2026-09-03 broad safe observation

- `HSR-4.5-MUMU_20260903_072523415` used the isolated safe set
  `GameObject(1), Material(21), MeshRenderer(23), Texture2D(28), MeshFilter(33),
  Mesh(43), Shader(48), AnimationClip(74), Animator(95), Light(108),
  LineRenderer(120)`. `Camera(20)` and `Transform(4)` were intentionally
  excluded because the former previously crashed during unsafe factory
  construction and the latter remains in the factory skip list.
- The session reached the Administrative District without a crash and emitted
  `405` GameObject, `19` Animator, `40` AnimationClip, `523` Shader,
  `7` Material, and `10` Texture2D transfer callbacks. The observer therefore
  works across startup and a real loaded scene, not only the login screen.
- The session still emitted zero accepted TypeTree trees. Its six diagnostic
  records contain raw Transfer layout evidence, but no field names or verified
  serialized asset identities. This is the current reproducible boundary.
