# MUMU 复用注入验证

## 目的

验证 `mumu --reuse-mumu` 在不重启 MUMU 的情况下，是否能把本地会话环境传给已运行的 `MuMuNxMain.exe`，再加载 Windows 桥接模块。

## 实验

- 目标宿主：`D:\Unpack_Workspace\Games_MUMU\MuMuPlayerGlobal\nx_main\MuMuNxMain.exe`
- 目标 PID：`38172`
- Profile：`HSR-4.5-MUMU`
- 模块：`RuntimeBridgeProbe`，仅用于注入协议验证
- 结果目录：`artifacts/HookSessions/reuse-probe-env-20260911`

## 结果

- MUMU 未被停止或重启。
- 工具按完整可执行路径匹配到唯一宿主 PID `38172`。
- 工具在目标进程内调用目标进程的 `kernel32!SetEnvironmentVariableW`，写入会话环境后再加载 DLL。
- `runtime/events.ndjson` 收到一条 `bridge_probe`，其中包含正确的本地事件文件路径、Profile 和 `captureSessionId`。
- 目标宿主仍在运行。

## 边界

该实验只证明 Windows 宿主注入和会话传递，不证明 HSR 客体 `TypeTreeRipper` 可以晚加载。
客体 TypeTree 仍必须在 Android 游戏进程启动前通过 `LD_PRELOAD` 注入；但这不等同于必须重启 MuMu VM。当前 CLI 的 `CaptureGuestTypeTree` 路径会设置 `wrap.<package>`，然后 force-stop 并重新启动目标 Android 包，因此可在复用 VM 时为新游戏进程加载 Ripper。

该“复用 VM + 重启目标包”的组合此前没有实机验证。后续状态不能从本实验推断成功；必须确认游戏进程在 `LD_PRELOAD` 下重启、Ripper 事件实际增长，并且 MuMu VM 本身保持运行。`--reuse-mumu` 仍不能与 RenderDoc 启动模式混用。
