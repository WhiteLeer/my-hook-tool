# HSR 4.5 R23 Runtime Readiness Check, 2026-09-17

## Read-Only Device State

Checked at `2026-09-17T19:19:32+08:00` through MuMu ADB serial
`127.0.0.1:16384` and `mumumanager info --vmindex 0`.

- MuMu VM index 0 is online and reports Android 15.0; ADB returns the device
  as `device`.
- `com.miHoYo.hkrpg` is installed as version `4.5.0`, process PID `15824`.
- The current top activity is `com.mihoyo.combosdk.ComboSDKActivity`; no
  assumption is made about whether the user has completed login.
- `/data/local/tmp/typetree-ripper.conf`, which the current MyHookTool
  orchestrator writes, is absent. A separate legacy
  `/data/local/tmp/guest-typetree-ripper.conf` exists and names capture session
  `HSR-4.5-MUMU_20260909_104855327-...`; it has `catalog_only=1`,
  `serialized_identity_observer=0`, `observe_transfers=0`, and `class_ids=21`.
- The guest ARM64 file `/data/local/tmp/my-hook-typetree-ripper-arm64.so` has
  SHA-256
  `b4b83fb081fe7a7b9d2478f196fa1e008ff8dfa96b2c72fe7e016a49762359a8`.
  The current R23 build in `runtime/HSR-4.5-MUMU/` has SHA-256
  `EE434AC8347AF7484CC92BA9BB889D0E448816B9B51F244C1269FB2E12798C95`.
  They do not match.
- Reading `/proc/15824/maps` and `/proc/15824/environ` as the ADB shell user is
  denied. Therefore the loaded module set is **not verified**. Do not infer it
  from files merely present under `/data/local/tmp`.
- No new local `events.ndjson` was created after the current R23 runtime build.

## Safe Preflight

Ran `tools/Run-HSR45NativeMaterialExperiment.ps1` with `-DryRun -ReuseMumu`
for serial `127.0.0.1:16384`, ClassIDs `21,48`, and without enabling native
TypeTree or guest raw patching. Profile/version validation passed. DryRun
confirmed that a real run would reuse MuMu and restart only the HSR Android
process to apply `LD_PRELOAD`; DryRun itself did not start MuMu or call a native
ABI.

## Decision

The R23 probes are **not active in the currently running HSR process** based on
the missing current orchestrator config, stale legacy guest config, mismatched
guest ARM64 library hash, and lack of post-build events. Loaded mappings cannot
be conclusively inspected because `/proc` access is denied, so this is an
operational readiness conclusion, not a claim that no instrumentation of any
kind is loaded.

Do not overwrite guest files or force-stop the active HSR process in this
readiness check. The next runtime test requires the controlled current
orchestrator run, which will replace the runtime/config and restart the HSR app
while preserving the MuMu VM. Capture a fresh session ID and verify its
event-file hash, install guards, and TypeTree output before interpreting any
resolver relation.

