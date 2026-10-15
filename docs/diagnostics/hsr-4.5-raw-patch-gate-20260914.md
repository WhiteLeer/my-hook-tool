# HSR 4.5 ARM64 Identity Raw-Patch Gate

## Confirmed

- `InstallGuestSerializedIdentityRawPatch` only proceeds when both
  `SR_SERIALIZED_IDENTITY_GUEST_PATCH=1` and
  `SR_TYPETREE_UNSAFE_EXPERIMENT=I_UNDERSTAND_UNITY_CRASH_RISK` are present.
  Evidence: `D:\Unpack_Workspace\References\TypeTreeRipper\source\android\android_impl.cpp`,
  `GuestSerializedIdentityRawPatchEnabled`, lines 4291-4297.
- The current HSR 4.5 experiment script now supplies the guest-patch flag and the
  safety token. Evidence: `tools/Run-HSR45NativeMaterialExperiment.ps1`, the
  `serialized-identity-guest-patch` and `unsafe-experiment` argument entries.

## Observed

- The latest existing session was created before that script change and records
  `rawPatch=disabled`; it is not evidence about the new configuration.
  Evidence: `artifacts/HookSessions/native-material-hsr45-runtime-resolve-20260914/HSR-4.5-MUMU_20260914_065838667/runtime/events.ndjson`,
  `unity.serialized-identity-observer-installed`.
- The current Android task stack has only
  `com.miHoYo.hkrpg/com.mihoyo.combosdk.ComboSDKActivity`, which is the launcher/SDK
  host Activity. This is process-location evidence only; it does not prove that a
  Unity scene is absent because Unity may be embedded in the same Activity.
  Evidence: `adb shell dumpsys activity activities`, captured by
  `tools/Show-HookStatus.ps1 -MumuRoot ... -Json` and the direct APK/activity audit.

## Hypothesis

- A fresh HSR 4.5 session started after the script change should either emit
  `unity.serialized-identity-raw-patch` or record a concrete installation failure;
  this must be checked from the new session, not inferred from the old one.

## Blocked

- The sourceFile/PathID, Material/Shader, and RenderDoc EID bridge remains
  unproven until a fresh session produces scene-level runtime evidence and a
  non-empty guest raw-patch or equivalent same-object evidence. No address,
  ordering, key64, runtimeValue32, or ResourceId interpretation is accepted.
