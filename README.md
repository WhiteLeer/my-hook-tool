# my-hook-tool

## Current Source Checkpoint

This repository contains both the historical runtime-hook research harness
and the current static resource/Shader validation utilities. They are separate
paths: static reconstruction does **not** require a `.hook` session or a live
injection, and does not prove an AS-object-to-RenderDoc-Draw identity bridge.

- `src/StaticBlockProbe` and `tools/hsr_static_container.py`: bounded static
  container inspection, with source-byte and version checks.
- `src/StaticShaderDecode` and the static Shader tools under `tools/`: original
  compiled-program decoding, exact metadata bindings and explicit preview
  adapters. These are not recovered original Shader source or a full game
  renderer.
- `unity/`: Unity sample builders and verification helpers. Some scripts are
  deliberately versioned local experiments, not portable one-click products.
- `docs/diagnostics`: historical evidence and limitations. The version on an
  experiment is its actual source version and must not be relabeled.

Current live-client work targets HSR 4.6. Public game naming is **HSR**;
historical file names, protocol identifiers and `.srrdc` capture extensions
remain unchanged where they identify existing data. Runtime binaries, captures
and game resources are excluded from source commits.

Build the CLI with .NET 8. StaticBlockProbe accepts `-p:AsBin=<AS CLI output>`;
StaticShaderDecode requires
`-p:SmolvSourceDir=<AnimeStudio.Utility/Smolv>`. Native experiment components
need their separately documented dependencies and must not be assumed to be
ready merely because the managed CLI builds.

## Runtime Research Status

`my-hook-tool` 用于封装 HSR/ZZZ 的运行时证据为 `.hook` 会话文件。
当前安装客户端为 HSR 4.6.0，活动配置是 `profiles/active-hsr.json` 指向的 `HSR-4.6-MUMU`。
4.6 已完成官方 APK 与实际安装版本核验，native ABI 和 TypeTree 尚待验证，当前为 `discovery-only`。
已验证的历史运行目标是 MuMu 中的 HSR 4.5，使用 `HSR-4.5-MUMU` Profile 在客体启动前加载
TypeTreeRipper，自动收集运行时目录和 Transfer 观测；Shader、Material 等对象记录
仍需对应客体桥接模块。4.5 会话保留为离线回归证据，新的实机工作只针对实际安装的 4.6。

本轮目标和验收顺序见 [`docs/current-goal.md`](docs/current-goal.md)。
通用启动入口为 [`tools/Start-HSR-MuMuHook.bat`](tools/Start-HSR-MuMuHook.bat)，读取活动 Profile；
新版本未通过 ABI 验证前不会沿用旧版本的 native 地址启动注入。

以下为历史 4.5 入口说明，不用于当前 4.6 客户端。历史启动入口：双击 [`tools/Start-HSR-4.5-MuMuHook.bat`](tools/Start-HSR-4.5-MuMuHook.bat)，
选择 MUMU 根目录、VM 和已安装的 HSR 包。输出默认位于仓库的 `artifacts/HookSessions`；
`.hook` 始终回写 Windows 本地会话目录，不写入 MUMU 客体。
如果 MuMu VM 已经运行，优先使用
[`tools/Start-HSR-4.5-MuMuHook-Reuse.bat`](tools/Start-HSR-4.5-MuMuHook-Reuse.bat)：它复用当前 VM，
只重启目标 HSR Android 进程以加载客体 Ripper，不会关闭整个模拟器。普通 BAT 用于需要完整重启
MuMu 的场景；不要在 VM 正处于 `stopping` 或 ADB 离线时启动任一注入流程。

版本 BAT 只是给通用入口传入 Profile；实际逻辑位于
[`tools/Start-MuMuHook.ps1`](tools/Start-MuMuHook.ps1)。因此迁移到另一台电脑时不需要
修改脚本中的盘符。也可以设置 `MY_HOOK_PROFILE` 后直接调用通用入口；MUMU 路径、输出
目录和 VM 编号会保存在当前 Windows 用户的 `%LOCALAPPDATA%\my-hook-tool`，不会进入 Git。
如果记忆的 MUMU 目录已因换盘失效，启动窗口会丢弃旧路径，并优先读取正在运行的
`MuMuNxMain.exe`；无法发现时只需重新浏览选择，不需要改 BAT。
如果保持本仓库与 `Games_MUMU\MuMuPlayerGlobal` 位于同一个工作区，脚本会按相对目录
自动发现 MUMU，因此整个工作区从 D 盘移动到 E 盘不需要改任何文件。
失效的旧输出目录也会自动回退到当前仓库的 `artifacts/HookSessions`，不会因换盘继续使用
旧盘符。启动 VM 使用 Profile 的 `mumuAndroidVersion`，默认是 `auto`，不会把另一台电脑的
Android 12/15 安装差异写死在脚本里。
ADB 地址默认从 MUMU CLI 按所选 VM 自动读取；只有自动映射不可用时才需要填写
`--adb-serial` 或设置 `MY_HOOK_ADB_SERIAL`，不会假定固定端口。

需要快速确认当前分支、Profile、MUMU 进程内存和最近会话时，可双击
[`tools/Show-HookStatus.bat`](tools/Show-HookStatus.bat)，或执行
[`tools/Show-HookStatus.ps1`](tools/Show-HookStatus.ps1)。它不会启动、停止或注入任何
进程；传入 `-MumuRoot <mumu-root>` 后还会逐设备报告 Android 前台 Activity，报告写入被
Git 忽略的 `artifacts/status/latest.json`，用于在长时间实验中恢复上下文。多个 ADB
设备会全部列出，不会按设备顺序猜测目标。HSR 的 Unity 运行时可能嵌在 SDK launcher
Activity 中，因此 Activity 名称只用于定位进程；场景就绪必须以运行时事件中的
`SkinnedMeshRenderer`、`ParticleSystem` 等场景级类型为准。

启动高风险 native TypeTree 实验前，可先运行
`my-hook-tool.exe profile-check .\profiles\HSR-4.5-MUMU.json`。该检查只验证活动版本、
Profile 的 native 槽位和 TypeTree baseline 哈希，不会启动 MuMu 或调用 native ABI。

Material/Shader TypeTree 实验使用 `tools/Run-HSR45NativeMaterialExperiment.ps1`；它默认拒绝执行，
只有显式传入 `-ConfirmRisk` 才会启动 HSR 4.5 客体，默认请求单个 ClassID `21`
（Material）。native ABI 实验必须一次只请求一个 ClassID；需要验证 Shader 时显式传入
`-GuestClassIds 48`，不要把 `21,48` 合并到一次 native 调用。运行前应先
确认 MuMu 可被本次实验占用；如果检测到多个 ADB 设备，还必须传入对应的
`-AdbSerial`。同一个 MuMu 实例暴露多个相同 boot fingerprint 的 ADB 端点时会自动去重，
并优先使用 MuMu 管理器报告的端点；不同实例仍必须显式指定。默认把 HSR 启动到
TypeTree 就绪的总预算限制在 120 秒；TypeTree 就绪后
自动进入后默认观察 30-60 秒，并在确认场景类型且事件稳定 15 秒后收尾；可用
`-GuestEventSeconds`、`-GuestEventMaxSeconds`、`-GuestEventIdleSeconds` 和
`-GuestTotalTimeoutSeconds` 调整。HSR Profile 会在 Unity 登录页按当前窗口尺寸自动点击一次，
整个启动、进入和采集流程默认限制在 120 秒内；未确认场景时也会在总时限到达后结束，
并在 `.hook` 中记录 `stopReason` 与自动进入动作结果，不把超时当作场景成功。guest identity raw-patch
默认关闭；只有显式传入 `-EnableGuestIdentityRawPatch`，并同时满足风险确认条件时才会
安装。caller post-store 观察可用 `-EnableIdentityCallerPatch -EnableGuestIdentityPatches`
单独启用，便于隔离 ABI 实验。两者都只应在可接受客体崩溃风险的隔离 MuMu 实例中执行；
仍只接受 HSR 4.5 Profile。
可先追加 `-DryRun` 检查设备选择和参数，确认无误后再移除该开关。

## 边界

- `attach`/`mumu` 负责运行时注入；`capture-session` 只负责把 RenderDoc 启动、会话目录和 Hook 环境变量编排在一起。
- `capture-session` 不会替代 RenderDoc 的触发机制，也不会自动制造有效的 Unity 资源身份；目标进程启动后仍需按现有流程触发一次捕获。
- RenderDoc 也可以只作为后续已有 `.srrdc` 离线分析器的输入，不参与普通注入。
- TypeTree 只描述字段结构，不能代替运行时对象识别。
- `.hook` 中的对象记录必须由桥接模块写入；客体 TypeTree 目录由 TypeTreeRipper
  运行时生成，不会伪造 Shader 或材质数据。

## 构建

```powershell
dotnet build `
  '.\src\MyHookTool\MyHookTool.csproj' -c Release
```

构建目标是 .NET 8。双击 4.5 启动脚本时，如果仓库内没有已构建的 EXE，脚本只会
按以下顺序寻找 .NET 8：`MY_HOOK_DOTNET`、仓库内 `dotnet\8`、仓库同级
`dotnet\8`、PATH；不会静默切换到其他主版本。也可以直接给脚本传入
`-DotnetPath <dotnet.exe>`。

如果需要同时重建客体运行库并部署到 4.5 Profile，使用仓库内的
[`tools/Build-HSR45Runtime.ps1`](tools/Build-HSR45Runtime.ps1)：

```powershell
& '.\tools\Build-HSR45Runtime.ps1' `
  -TypeTreeRepository '<path-to-TypeTreeRipper>'
```

脚本固定使用 Android x86_64 的 `RelWithDebInfo` 预设和 .NET 8，完成构建、复制
`libTypeTreeRipper.so`、更新 SHA-256 manifest；重复执行时不会因为时间戳产生无意义变化。
也可用 `-DotnetPath`、`-CMakePath` 覆盖本机工具路径，或用 `-SkipNative` 只重建托管端。

跨电脑的无窗口调用示例：

```powershell
$env:MY_HOOK_PROFILE = '<profile-id>'
$env:MY_HOOK_DOTNET = '<path-to-dotnet-8>\dotnet.exe'
& '.\tools\Start-MuMuHook.ps1' -NoDialog `
  -MuMuRoot '<mumu-root>' `
  -OutputDirectory '.\artifacts\HookSessions'
```

## 注入会话

对 Windows 目标进程以挂起方式启动，加载桥接 DLL 后恢复进程：

```powershell
& '.\src\MyHookTool\bin\Release\net8.0\my-hook-tool.exe' attach `
  --profile '.\profiles\HSR-4.5-MUMU.json' `
  --target '<mumu-root>\nx_main\MuMuNxMain.exe' `
  --module '<path-to-bridge>\hsr-runtime-bridge.dll' `
  --output '<hook-output-directory>'
```

也可以注入已经运行的进程：

```powershell
my-hook-tool.exe attach --profile .\profiles\HSR-4.5-MUMU.json `
  --target-pid 1234 --module .\hsr-runtime-bridge.dll `
  --output .\HookSessions
```

如果 MUMU VM 已经运行，可使用 `mumu --reuse-mumu` 避免关闭整个模拟器。HSR 4.5
Profile 默认启用客体 Ripper；复用模式会注入已运行的唯一 `MuMuNxMain.exe`，设置 guest
`wrap`，然后只重启目标 Android 游戏进程，使 `LD_PRELOAD` 在游戏启动时生效。若使用
不需要客体 Ripper 的纯宿主注入，才显式指定 `--no-guest-ripper`；这不会产生客体 TypeTree。
当 VM 未运行时使用普通启动流程。VM 处于 `stopping`、ADB offline 或其他非健康状态时，先通过
MuMu 正常恢复 VM，不要用重复启动或强杀进程来绕过状态检查。

```powershell
my-hook-tool.exe mumu `
  --profile .\profiles\HSR-4.5-MUMU.json `
  --mumu-root '<mumu-root>' `
  --output .\artifacts\HookSessions `
  --reuse-mumu --launch-package com.miHoYo.hkrpg
```

命令会生成 `<session>/<session>.hook` 和
`<session>/runtime/events.ndjson`。`.hook` 在注入后立即创建，桥接模块按
[`docs/runtime-bridge.md`](docs/runtime-bridge.md) 追加记录，之后执行：

```powershell
my-hook-tool.exe finalize '<session>\<session>.hook'
my-hook-tool.exe inspect '<session>\<session>.hook'
my-hook-tool.exe analyze '<session>\<session>.hook'
```

捕获完成后，不要默认执行整帧 Shader 导出；大帧可能占用数 GB 内存。对已经确认的
RenderDoc EID，可用 `enrich` 增量补充 Shader、变体、阶段、反射和可选资源快照：

```powershell
my-hook-tool.exe enrich `
  '<session>\session.hook' `
  --renderdoc '<path-to-renderdoc>\renderdoccmd.exe' `
  --event 436 `
  --reconstruct `
  --spirv-cross '<path-to-spirv-cross>\spirv-cross.exe'
```

`--event` 可以重复指定多个 EID；这是有意的强制参数，工具不会因为省略 EID 而扫描
整帧。导出结果写入 `.hook` 同级的 `renderdoc/events-<EID>/`，并记录
`shaderHash`、`interfaceHash`、阶段、ResourceId、变体和反射文件的相对路径。
这些是捕获内确定的 GPU 身份，不能单独推导 AnimeStudio 的原始资源路径或材质名；
跨 AS、运行时和 RenderDoc 的关联仍必须使用同一会话的证据或显式 link-map。

`analyze` 不会修改 `.hook`，会生成同目录下的
`<session>.analysis.json`。输出包括记录总数、按 `typeName` 的计数、唯一对象、
Transfer、vtable 数量、对象快照覆盖率，以及快照中确实命中已观测地址的指针引用。
如果存在 ARM64 guest raw-patch，报告还会原样保留其 mailbox 记录和计数，但不会替其中
的 `key`、返回值或地址赋予 PathID/InstanceID 语义。
它不会把未知字节猜测为资源路径、Hash、Shader 名称或材质字段；这一步是后续
TypeTree 字段解码和资源关联的输入检查。

## 三方对齐

`align` 用于把同一次分析的 RenderDoc、AnimeStudio 和运行时 Hook 证据整理到
一个 `my-hook.alignment.v2` 清单。它不修改输入文件，也不把猜测结果写成关联：

```powershell
my-hook-tool.exe align `
  --capture '.\frame3630\renderdoc\capture.json' `
  --as-report '.\frame3630\as\shader-report.json' `
  --as-bindings '.\frame3630\as\unity-material-bindings.json' `
  --runtime-hook '.\hsr-post-login-full.hook' `
  --hook '.\frame3630\mumu-host_sr_frame3630.hook' `
  --output '.\Alignment\frame3630-hsr44-alignment.json'
```

清单会分别保存：RDC 的 EID、阶段、ResourceId、Shader/Interface/Variant 标识及
绑定资源；AS 的 Shader/Material 名称、PathID、关键字和材质参数；Hook 的运行时
类型、对象、Transfer、vtable 统计和 TypeTree 诊断。RDC 内部和 AS 内部的关联是
精确关联；RDC↔AS、Hook↔AS/RDC 没有共同身份时会列在 `unresolved`，不会按名称、
地址或顺序推断。只有通过校验的 `--link-map` 才会产生 `explicit` 跨源关联。

需要复用 AS 的完整资产身份时，可先生成索引，再在 `align` 中通过
`--as-index .\as-asset-index.json` 载入：

```powershell
my-hook-tool.exe as-index '.\as-output' `
  --shader-report '.\shader-report.json' `
  --as-bindings '.\unity-material-bindings.json' `
  --output '.\as-asset-index.json' --force

my-hook-tool.exe align `
  --capture '.\frame\renderdoc\capture.json' `
  --hook '.\frame\mumu-host_sr_frame3630.hook' `
  --runtime-hook '.\runtime.hook' `
  --output '.\alignment.json'
```

在需要把多条验收命令收束为一个可审计结果时，使用 `verify`。它不会修改输入，
只会生成一个 JSON 报告；退出码 `0` 表示全部证据通过，`1` 表示输入无效，`2`
表示仍有部分覆盖或运行时阻塞：

```powershell
my-hook-tool.exe verify `
  --as-index '.\as-asset-index.json' `
  --typetree-report '.\composite.typetree-report.json' `
  --analysis '.\runtime.analysis.json' `
  --profile '.\profiles\HSR-4.5-MUMU.json' `
  --hook '.\capture.hook' `
  --output '.\verification.json'
```

`verify` 分别检查 AS 的 `sourceFile#pathId` 身份和路径、Material→Shader/Texture
端点、关键 TypeTree 字段，以及运行时 `identityBridge`。未提供运行时分析、版本
不一致或没有经过验证的 `unity.draw-context` 时会显示 `blocked`，不会把离线基线
提升为运行时结论；追加 `--require-full-catalog` 可将完整 RTTI catalog 覆盖作为
强制门槛。

仓库内也提供等价的 `tools/Verify-HSR-4.5-MuMu.ps1`，适合双击外的固定命令行
流程；它只负责组装参数并调用同一个 .NET 8 CLI。完整状态、退出码和不推断原则见
[`docs/verification.md`](docs/verification.md)。

当 `--hook` 是由 `export` 生成且包含 `as/` 文件时，`align` 会自动读取其中的
Shader 报告、Material 绑定和 AS 资产索引；也可以继续显式传入 `--as-report`、
`--as-bindings` 或 `--as-index` 覆盖嵌入文件。自动读取不会把 RDC ResourceId、
GPU hash 或运行时地址转换成 AS 身份。

`export` 生成的 `.hook` 的 `as` 节点会记录 AS 资产索引、路径校验状态、资产数量，
以及 Material→Shader/Texture 绑定数量；完整绑定和路径证据保存在同目录的 `as/` 下，
便于复核而不把巨大的原始 Block 写进 `.hook`。

索引只增加经过 `SourceCAB#PathID` 校验的 AS 内部绑定，不会把 RDC 的 ResourceId、
ShaderHash 或运行时地址猜成 AS 身份。资产自身路径与引用它的 GameObject 上下文
分别记录在 `paths` 和 `usagePaths`。

索引还会输出 `pathValidation`：它保留 AS 原始路径证据，统计绝对路径与相对路径，
并拒绝包含 `..` 的目录穿越路径。路径证据和资产身份是两件事；精确身份始终使用
`sourceFile#pathId`。

AS 输出中的 `assetIdentity.key` 使用 `<sourceFile>#<pathId>`，是序列化资产域内
的精确身份。Material→Shader 和 Material→Texture 关系直接来自 AnimeStudio 的
序列化引用；`source.originalPath` 只是 AS 读取源文件的本机证明，不是可移植的
运行时资源路径。RDC 的 ResourceId、Hook 的 native 地址和 AS 的 PathID 仍是
不同身份域，未取得共同运行时证据前不会自动互绑。

`link-map` 是 JSON 数组，每项至少包含一个当前 capture 中存在的
`shaderHash` 或 `variantHash`，以及 AS 报告中存在的完整
`asShaderIdentityKey`。仅写 Shader 名称不再被接受，因为不同 CAB 可以出现同名
Shader 或相同 PathID；名称只能作为经过校验的显示字段：

```json
[
  {
    "variantHash": "capture 中实际存在的 variantHash",
    "asShaderIdentityKey": "AS 报告中 assetIdentity.key 的完整值",
    "asShaderName": "可选，必须与该身份对应的名称一致"
  }
]
```

ResourceId 只在单个 RDC 内有效，AS PathID 是 Unity 资产身份，二者不能直接互换。
当前 HSR 样本的对齐结果和格式说明见 [`docs/alignment.md`](docs/alignment.md)。
HSR 4.5 是当前唯一的实机目标。HSR-4.4 的完整性边界和可复现负例仅作为归档参考，见
[`docs/diagnostics/hsr-4.4-mumu-closure-20260831.md`](docs/diagnostics/hsr-4.4-mumu-closure-20260831.md)，
不再用于登录、注入或实验。

如果 `.hook` 已附带 `TypeTreeRipper.ttbin`，`analyze` 会按其版本 1 二进制布局
解码 RTTI、TransferFlags 和每个节点的 `type`、`name`、`level`、`byteSize`、
`metaFlags`、`refTypeHash`。也可以用 `--typetree <file.ttbin>` 显式分析外部文件；
解码结果写入分析 JSON 的 `decodedTypeTree`，不会改写原始 `.ttbin`。
如果文件结构有效但没有字段节点，状态为 `decoded_empty`；这代表安全观察模式
只写出了 RTTI/树骨架，不能用于对象字段解码。

也可以直接校验外部 TypeTree 的字段覆盖率：

```powershell
my-hook-tool.exe typetree .\release.ttbin `
  --catalog .\rtti-catalog.txt `
  --required-type Shader `
  --required-type Material
```

`complete_for_requested_types` 只表示指定类型实际包含字段节点，不代表整个运行时
都已覆盖。当前 HSR MuMu 会话生成的空 `.ttbin` 必须保持 `decoded_empty`，不能用
文件大小、RTTI 类型数量或进程存活状态替代字段树证据。

含有 Profile 基准的会话，分析 JSON 会同时输出 `decodedTypeTree`（客体实时产物）
和 `decodedTypeTreeBaseline`（离线基准）；两者不会互相覆盖，分别保留状态和来源。

需要逐节点字段时在上述命令追加 `--full`；报告会增加 `decodedTrees`，保留每个
节点的类型、名称、层级、版本、大小、索引、flags、`refTypeHash`，以及由层级计算的
`parentIndex`/`fieldPath`。

仓库随运行时包提供一份离线基准树：它由 Unity 2019.4.34 的 legacy
StructDump 与已验证的 Shader TypeTree 合并而成，包含 320 棵树和 21,143
个字段节点。它的来源和 SHA-256 记录在同目录的 `.manifest.json` 中；它是可复用的
字段解码基线，不代表当前 MuMu 客体本次实时生成了这些字段。当前可运行 Profile
对应的安装包已观察到客户端版本 `4.5.0`，因此不能把该文件称为某个旧版本客户端的
运行时快照。

如果只有 legacy StructDump，可先导入统一格式：

```powershell
my-hook-tool.exe typetree import-struct .\input.structs.dump .\output.ttbin `
  --catalog .\rtti-catalog.txt --force
```

该命令逐字段保留类型、名称、层级、版本、大小、索引和 meta flags；legacy 文本没有
`refTypeHash` 时保持为零，并在自动生成的 manifest 中明确标记。合并不同来源时默认
遇到冲突即失败；明确指定 `--prefer-more-nodes` 时也会先进行字段结构校验，只有
结构有效的候选才可被选择；双方都有效时才按节点数取舍，并把选择或拒绝写入 manifest：

```powershell
my-hook-tool.exe typetree merge .\composite.ttbin `
  .\legacy.ttbin .\shader.ttbin .\particle.ttbin `
  --prefer-more-nodes --force
```

如果需要附加外部 TypeTree 文件，可在整理会话时显式指定：

```powershell
my-hook-tool.exe finalize '<session>\<session>.hook' `
  --typetree 'D:\path\to\release.ttbin'
```

`.hook` 会记录 TypeTree 的来源、格式、大小和 SHA-256；它不会把 TypeTree
误标记为材质或 Shader 数据。

定位客体 TypeTree 入口时，可以先对 Android ARM64 `libunity.so` 做离线候选扫描：

```powershell
my-hook-tool.exe typetree-static '<path-to-libunity.so>' `
  --output '.\artifacts\analysis\typetree-static.json' `
  --window-bytes 1024
```

该命令只读取 ELF section、扫描固定字符串锚点，并在引用后的有限窗口中列出直接
`BL` 目标。结果明确标为 `candidate` 或 `candidate-page-only`，不能据此直接调用
函数、命名为 `GetTypeTree` 或生成字段树。不同客户端版本必须分别扫描；地址、调用
目标和字符串集合都不保证稳定。它的用途是缩小下一轮静态/运行时实验范围，而不是
替代字段级 TypeTree 证据。

## RenderDoc 协同会话

当需要把一次 RenderDoc 捕获与同一轮运行时 Hook 证据放在一起时，可使用：

```powershell
my-hook-tool.exe capture-session `
  --renderdoc '<path-to-renderdoc>\renderdoccmd.exe' `
  --target '<mumu-root>\nx_main\MuMuNxMain.exe' `
  --module '<path-to-bridge>\my-hook-runtime-probe.dll' `
  --output '<hook-output-directory>'
```

该命令会创建唯一的 `captureSessionId`，生成 `<session>/<session>.hook`、
`runtime/events.ndjson` 和默认的 `capture.srrdc` 路径，并将会话编号传给宿主
Hook。使用客体 Ripper 的 `mumu` 命令时，会话编号也会写入
`guest-typetree-ripper.conf`，因此客体 TypeTree 事件会带有同一编号。
会话编号只证明证据属于同一编排会话，不等于已经完成 Draw、Material 和
Shader PathID 的映射；`align` 仍只接受验证过的运行时身份或显式 link-map。

`capture-session` 当前会等待 RenderDoc 启动命令返回 TargetControl 端口，随后通过
TargetControl 查询真实目标 PID，再加载宿主模块。需要自动请求捕获时，`mumu` 可使用
`--auto-trigger`；它只请求一次，不会在后台高频重复触发。HSR Profile 会把捕获目标
设为实际承载 Vulkan 绘制的 `MuMuNxDevice`，而不是只负责启动的 `MuMuNxMain`。
完成捕获后，可使用 `align` 的现有命令将 RDC 导出、AS 报告和 `.hook` 严格对齐。

SR 分支也提供 MUMU 重启和客体早期加载编排。该命令会停止指定 VM，启动
`MuMuNxMain.exe`，启动 VM，并在目标 Android 程序进程创建前通过 `wrap` 加载
客体 TypeTreeRipper：

```powershell
my-hook-tool.exe mumu `
  --profile .\profiles\HSR-4.5-MUMU.json `
  --mumu-root '<mumu-root>' `
  --vmindex 0 `
  --output '<hook-output-directory>' `
  --launch-package 'com.miHoYo.hkrpg'
```

如果还要让 RenderDoc 负责启动 MUMU 宿主，并让宿主桥接与客体 TypeTree 共用一个
会话，可额外指定 `--renderdoc` 和 `--module`。Profile 会自动解析仓库内匹配的
客体 Ripper；需要覆盖时再显式指定 `--guest-ripper`：

```powershell
my-hook-tool.exe mumu `
  --profile .\profiles\HSR-4.5-MUMU.json `
  --mumu-root '<mumu-root>' `
  --vmindex 0 `
  --renderdoc '<path-to-renderdoc>\renderdoccmd.exe' `
  --module '<path-to-bridge>\my-hook-runtime-probe.dll' `
  --guest-ripper '.\runtime\HSR-4.5-MUMU\libTypeTreeRipper.so' `
  --auto-trigger `
  --output '<hook-output-directory>' `
  --launch-package 'com.miHoYo.hkrpg'
```

这里的 `--module` 是 Windows 宿主 DLL，`--guest-ripper` 是 Android 客体 `.so`；
两者可以在同一会话中同时使用，但不能互相替代。RenderDoc 负责启动并注入宿主层，
客体层由 MUMU `wrap` 流程加载。使用 `--no-guest-ripper` 可明确切换为仅宿主模式。
指定 `--auto-trigger` 后，工具通过随 Profile 提供的
`my-hook-renderdoc-trigger.exe` 连接 RenderDoc TargetControl，并在客体初始化流程
完成后向 Profile 指定的实际绘制目标请求一次捕获，然后等待捕获文件稳定落盘；也可用
`--trigger-tool <exe>` 指定自定义触发器。触发器会把
选定的 `renderdoc.dll` 隔离复制到临时运行目录，避免修改用户的 RenderDoc 安装目录。
等待超时不会伪造成功，`.hook` 会标记为 `capture_pending` 并保留捕获模板，
可稍后检查目标目录；可用 `--capture-timeout-seconds` 调整等待窗口。
不指定 `--auto-trigger` 时仍可使用 RenderDoc 热键手动捕获。

也可以在游戏已经进入场景后手动触发一次，并把完成的捕获回写到现有 `.hook`：

```powershell
my-hook-tool.exe trigger `
  --renderdoc '<path-to-renderdoc>\renderdoccmd.exe' `
  --target MuMuNxDevice `
  --frames 1 `
  --capture-file '<session>\capture.srrdc' `
  --hook '<session>\session.hook'
```

触发器会等待 RenderDoc 的 `NewCapture` 消息，并在收到后复制文件；仅收到“请求已发送”
不会被标记为捕获成功。目标名来自 RenderDoc 的实际 TargetControl 名称，不能用主进程
名代替 Vulkan 绘制进程名。

`my-hook-renderdoc-trigger.exe` 必须使用与目标 `renderdoc.dll` 兼容的本地
RenderDoc 源码和 API 构建。工具会将所选 DLL 及运行库隔离复制到临时目录，
但不会修复不同 RenderDoc 构建之间的 ABI 不兼容；不要把其他版本的触发器和
本 Profile 的 `renderdoc.dll` 混用。

命令成功后会在会话目录中生成 `.hook`，并包含
`runtime/typetree/rtti-catalog.txt`。Ripper 已随仓库放在
`runtime/HSR-4.5-MUMU/`，Profile 会自动解析并校验它；`--guest-ripper` 仅用于
开发时临时覆盖。当前 HSR 4.5 的默认配置为 Unity 2019.4.34f1。该 MuMu 实例的 Android
`primaryCpuAbi` 是 `arm64-v8a`，但进程由 x86_64 Native Bridge 承载，因此 `wrap`
必须加载 x86_64 版本的客体库；仓库内的运行时包已按这个实际加载 ABI 配置。
TypeTreeRipper 会等待客体初始化后再扫描。HSR-4.5 Profile 还会先等待客体
事件证明 Unity 原生运行时已经加载：优先使用 `runtime-events` 中的
`native-bridge-extra-load`/`libil2cpp.so` 记录或真实 Unity 观察事件，设备允许时
再用 `/proc/<pid>/maps` 作为辅助检查。仅停留在 SDK 登录界面时不会开始身份实验，
避免把登录阶段的空结果误判为解析失败。
客体输出随后由 ADB 拉回 Windows，`.hook` 不会存放在 MUMU 中。若要让游戏继续
运行并等待进入实际场景，默认会把启动阶段限制在 120 秒，并保留 30 秒初始观察窗口和最多 120 秒的登录/启动窗口；这段时间内由客体
观察到的 `unity.transfer` 事件会保存到 `.hook.records`，并附带 RTTI 已确认的
类型 ID、类型名、虚表地址和原始参数。它们是运行时证据，不等同于资源路径、Shader
或材质绑定；后续仍需基于这些证据实现对象到资源的明确映射。Profile 默认把最大
观察窗口设为 120 秒，并在已经观察到网格、粒子、Shader 或动画等场景类型后，等
事件稳定 15 秒再收尾；可用 `--guest-event-max-seconds` 和
`--guest-event-idle-seconds` 覆盖。登录页或黑屏加载阶段不会被误判为完整结果，
最终 `.hook.typetree.events.coverage` 会标记为 `startup-only` 或 `scene-observed`。

仓库内的一键脚本
[`tools/Start-HSR-4.5-MuMuHook.bat`](tools/Start-HSR-4.5-MuMuHook.bat)
会启动上述客体 TypeTree 流程。窗口会记住 MUMU、输出目录、VM 编号和
目标包名；选择目标包后会自动启动它，完成 TypeTree 获取后结束，不需要手动按键。

窗口中的“目标程序”列表由当前 VM 的 `pm list packages -3` 返回值生成，显示的
是实际包名。选择包名后，工具会在 VM 启动、宿主注入完成且 Android 客体可用时，
通过 MuMu CLI 请求启动该程序；选择“不自动启动”则保持原来的只启动 VM 流程。
如果 VM 尚未运行或 ADB 尚未上线，列表会显示不可用提示；启动 VM 后点击“刷新”
即可重新读取，工具不会根据包名猜测程序名称。

这里的“自动采集”不是定时重复扫描：每次启动只执行一次延迟扫描并写出目录。
如果需要 Shader、Material、贴图或资源路径，还必须加载与该 Unity 版本匹配的
Android/Unity 客体桥接模块；当前 TypeTreeRipper 本身只负责 TypeTree 目录。

如果只需要判断当前 Unity 版本的对象 vtable 入口，可额外传入
`--vtable-probe 1`。该模式只创建配置的 ClassID 对象并记录 vtable 槽位，随后跳过
native TypeTree 调用，不会把旧版本的 ABI 或地址当作 HSR 4.5 结论；它默认关闭，
也不能产生字段级 TypeTree。

## 分支

- `main`：保留基础实现，不作为游戏定制开发分支。
- `HSR`：长期维护的 HSR 开发分支，当前客户端目标为 4.6；使用活动 Profile 和通用启动入口，不以分支名推断运行时兼容性。
- `ZZZ`：独立的 ZZZ 开发分支，目前仅保留 3.0 配置脚手架，尚未完成运行时验证；不复用 HSR 偏移或字段。
- 版本更新保留独立 Profile、二进制哈希、ABI 检查和证据记录，不再新建带版本号的开发分支。`HSR-4.6-MUMU` 等 Profile ID 不是 Git 分支名，继续保留。
- 原带版本号的分支提交保存为 `archive/branches/<原分支名>` 标签；历史 4.4/4.5 资料保留真实版本，不重新标记为 4.6。

详细的事件格式和数据来源约束见 [`docs/runtime-bridge.md`](docs/runtime-bridge.md)。
