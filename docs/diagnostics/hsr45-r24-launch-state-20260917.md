# HSR 4.5 R24 Launch State

## Scope

Attempted to load the newly built trampoline-guard fix in a separate session
without modifying the R23 evidence. This is a launcher/VM state report, not a
runtime observer result.

## Confirmed

- The CLI created
  `artifacts/HookSessions/hsr45-trampoline-guard-r24-live-20260917/HSR-4.5-MUMU_20260917_122113018/`.
- `runtime/events.ndjson` is empty (0 bytes); no hook record was finalized.
- The configured `mumu` start workflow issued its documented shutdown sequence
  and timed out after 60 seconds in `WaitForMumuStopped`.
- Repeated `mumu-cli info --vmindex 0` responses report
  `player_state=stopping`, `is_process_started=true`, and
  `is_android_started=false`. ADB shell commands do not return.
- `mumu-cli control --vmindex 0 --version 15 restart` returned `-502`
  (`mainnx request failed`). The regular `launch` request returned success but
  the state remained `stopping`.
- No MuMu host or device process was force-killed. The MuMu main process and
  background device/service processes remain present.
- The launcher shutdown order was corrected in `src/MyHookTool/Program.cs`:
  it now waits for explicit VM stop before closing MuMu main. The .NET 8 build
  passes with zero warnings/errors. This code change has not yet been exercised
  against a healthy VM because VM 0 remains in `stopping`.

## Interpretation

R24 did not reach library injection or HSR startup. Its empty event file is not
a failed guard observation and says nothing about the corrected runtime probe.
The current blocker is MuMu VM recovery, not TypeTree or identity semantics.

## Next Gate

Restore VM 0 through MuMu's normal UI/control path and verify ADB plus
`is_android_started=true`. Then use the existing `--reuse-mumu` path so the
next experiment restarts only the HSR Android package instead of shutting down
the whole VM. If a full VM restart is unavoidable, the corrected shutdown
ordering is ready for a later controlled test. Keep R23 and this empty R24
directory intact.
