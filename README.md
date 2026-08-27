# my-hook-tool

`my-hook-tool` 用于在 HSR/ZZZ 的运行时注入桥接模块，收集 Unity 对象层的
Shader、Material、贴图、Renderer 和资源标识，并保存为 `.hook` 会话文件。
当前先实现 `SR-4.4-MUMU`，`ZZZ-3.0-MUMU` 仅保留分支配置。

## 边界

- 本工具当前不负责截取 RDC。
- RenderDoc 只作为后续已有 `.srrdc` 离线分析器的输入，不参与本阶段注入。
- TypeTree 只描述字段结构，不能代替运行时对象识别。
- `.hook` 中的运行时记录必须由桥接模块写入；没有桥接模块时只生成会话壳，
  不会伪造 Shader 或材质数据。

## 构建

```powershell
& 'D:\Unpack_Workspace\Tools\dotnet\8\dotnet.exe' build `
  '.\src\MyHookTool\MyHookTool.csproj' -c Release
```

## 注入会话

对 Windows 目标进程以挂起方式启动，加载桥接 DLL 后恢复进程：

```powershell
& '.\src\MyHookTool\bin\Release\net8.0\my-hook-tool.exe' attach `
  --profile '.\profiles\HSR-4.4-MUMU.json' `
  --target 'D:\path\to\MuMuNxMain.exe' `
  --module 'D:\path\to\hsr-runtime-bridge.dll' `
  --output 'D:\Unpack_Workspace\HookSessions'
```

也可以注入已经运行的进程：

```powershell
my-hook-tool.exe attach --profile .\profiles\HSR-4.4-MUMU.json `
  --target-pid 1234 --module .\hsr-runtime-bridge.dll `
  --output .\HookSessions
```

命令会生成 `<session>/<session>.hook` 和
`<session>/runtime/events.ndjson`。`.hook` 在注入后立即创建，桥接模块按
[`docs/runtime-bridge.md`](docs/runtime-bridge.md) 追加记录，之后执行：

```powershell
my-hook-tool.exe finalize '<session>\<session>.hook'
my-hook-tool.exe inspect '<session>\<session>.hook'
```

如果同时运行了 TypeTreeRipper，可在整理会话时附加其输出：

```powershell
my-hook-tool.exe finalize '<session>\<session>.hook' `
  --typetree 'D:\path\to\release.ttbin'
```

`.hook` 会记录 TypeTree 的来源、格式、大小和 SHA-256；它不会把 TypeTree
误标记为材质或 Shader 数据。

SR 分支也提供 MUMU 重启和早期注入编排。该命令会停止指定 VM、挂起启动
`MuMuNxMain.exe`、加载桥接 DLL、恢复宿主，再启动 VM：

```powershell
my-hook-tool.exe mumu `
  --profile .\profiles\HSR-4.4-MUMU.json `
  --mumu-root 'D:\Unpack_Workspace\Games_MUMU\MuMuPlayerGlobal' `
  --vmindex 0 `
  --module 'D:\path\to\hsr-runtime-bridge.dll' `
  --output 'D:\Unpack_Workspace\HookSessions'
```

`mumu` 只负责 Windows 宿主侧加载模块，不等于已经进入 Android 客体。客体
桥接模块和客体注入器必须另行提供。

仓库内的一键脚本
[`tools/Start-HSR-4.4-MuMuHook.bat`](tools/Start-HSR-4.4-MuMuHook.bat)
会启动上述 `mumu --watch` 流程。它会在注入后持续等待，不需要像 RenderDoc
一样手动按键截帧；桥接模块产生的新事件会追加到会话中。按 `Ctrl+C`，或目标
进程退出时，工具会自动将事件合并回 `.hook` 并结束会话。也可以给 CLI 传入
`--duration-seconds N`，在 N 秒后自动收尾。

这里的“自动采集”不是定时重复扫描：每条运行时记录只写入一次。当前仓库附带的
`my-hook-runtime-probe.dll` 仅用于验证 Windows 宿主注入链路，记录内容是
`bridge_probe`，不代表已经采集到 Unity 的 Shader、材质或 TypeTree。要获取这些
客体数据，仍需实现并加载对应的 Android/Unity 客体桥接模块。

## 分支

- `SR-4.4-MUMU`：HSR 4.4 运行时桥接协议和注入配置。
- `ZZZ-3.0-MUMU`：预留 ZZZ 3.0 运行时桥接配置，不复用 HSR 偏移或字段。

详细的事件格式和数据来源约束见 [`docs/runtime-bridge.md`](docs/runtime-bridge.md)。
