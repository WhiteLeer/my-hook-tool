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
`<session>/runtime/events.ndjson`。桥接模块按
[`docs/runtime-bridge.md`](docs/runtime-bridge.md) 追加记录，之后执行：

```powershell
my-hook-tool.exe finalize '<session>\<session>.hook'
my-hook-tool.exe inspect '<session>\<session>.hook'
```

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

## 分支

- `SR-4.4-MUMU`：HSR 4.4 运行时桥接协议和注入配置。
- `ZZZ-3.0-MUMU`：预留 ZZZ 3.0 运行时桥接配置，不复用 HSR 偏移或字段。

详细的事件格式和数据来源约束见 [`docs/runtime-bridge.md`](docs/runtime-bridge.md)。
