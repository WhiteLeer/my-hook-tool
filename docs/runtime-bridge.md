# Runtime Bridge Protocol

## HSR 4.5 目标

本工具只接受一条可验证的数据链：运行时 TypeTree/字段证据 -> 真实 Material/Shader
对象身份 -> 同一 RenderDoc capture 的 EID。每一段都必须来自同一对象或同一调用链；
缺少任一段时，`.hook` 保持 `partial`/`blocked`，不会用名称、地址、顺序、
`ResourceId` 或 hash 补齐。

运行时桥接模块由目标进程加载，使用环境变量找到当前会话：

- `MY_HOOK_PROFILE`：Profile ID。
- `MY_HOOK_OUTPUT`：runtime 输出目录。
- `MY_HOOK_EVENTS`：NDJSON 事件文件的绝对路径。
- `MY_HOOK_FILE`：当前 `.hook` 文件路径。
- `MY_HOOK_CAPTURE_SESSION_ID`：本次 RenderDoc/运行时编排会话的唯一编号；它只
  用于证明采集批次一致，不能替代 Unity Material/Shader PathID 或 RenderDoc EID。

TypeTreeRipper 输出可以在会话结束时通过 `finalize --typetree` 挂入：

```powershell
my-hook-tool.exe finalize '<session>\<session>.hook' `
  --typetree 'D:\path\to\release.ttbin'
```

文件会复制到 `.hook` 会话的 `runtime/typetree/`，并记录 TypeTree 格式、大小、
SHA-256 和原文件名。`.ttbin` 会在挂接时做结构校验，并记录 `treeCount`、
`totalNodes` 和 `status`；空树会标为 `decoded_empty`，不会被当成完整字段树。
`analyze` 会解出节点字段，`typetree` 命令还可按 RTTI 目录检查覆盖率。`.tpk`
或结构文本仍保留为原始证据，转换继续使用 TypeTreeRipper 自带 Converter。

`mumu --vtable-probe 1` 是安全的入口诊断模式：它只记录当前客体创建对象的
vtable 观察结果并跳过 native TypeTree 调用。对应的有界 Android logcat 会保存为
`.hook` 内的 `runtime/typetree/ripper-logcat.txt`，并记录大小和 SHA-256；它是
ABI 定位证据，不是字段树，也不能单独用于 Material/Shader 对齐。

Profile 还可以提供 `typeTreeBaseline`。`mumu` 运行时若客体只生成目录或空树，
会将该基准复制到本地会话的 `runtime/typetree/baseline.ttbin`，并标记为
`offline_baseline`。它只表示同 Unity revision/variant 的已验证离线字段来源，
不表示本次客体实时生成了完整树；实时产物仍单独保留并可为 `decoded_empty`。

HSR 4.5 Profile 还记录了已通过静态首指令校验的 PPtr 与
`serialized-object-resolve-candidate` 观察参数。普通 `mumu` 入口会从 Profile
读取这些参数，实验脚本和普通启动因此使用同一版本配置；它们仍只产生 opaque
调用链证据，不代表已经得到 FileID、PathID、InstanceID 或 Material/Shader 身份。

桥接模块以 UTF-8、逐行追加的方式写入 `MY_HOOK_EVENTS`。采集期间不需要工具
反复重建 `.hook`；工具只监听目标进程生命周期，结束时再读取事件文件一次。
每一行必须是一个 JSON 对象，且包含 `schema`、`kind`、`source` 和 `payload`。
当前运行时事件还会附带单调递增的 `eventSequence` 和 Linux/Android
`threadId`；二者只用于调用边界和线程归属审计，不是 Unity 对象 ID、PathID、
InstanceID 或 RenderDoc EID：

```json
{
  "schema": "my-hook.runtime-event.v1",
  "kind": "material",
  "eventSequence": 1234,
  "threadId": 9876,
  "source": {
    "layer": "unity-runtime",
    "profile": "HSR-4.5-MUMU"
  },
  "payload": {
    "instanceId": 123,
    "name": "Eff_Glow_44_OneChannel_15",
    "shaderName": "miHoYo/CRP_Particles/Particle_OneChannel",
    "shaderPathId": -7177827563780441733,
    "properties": {},
    "textures": []
  }
}
```

`eventSequence` 由 Hook 进程内的事件写入点分配，不能替代调用栈或同一函数参数；
`threadId` 只能说明事件发生在哪个客体线程。只有事件明确记录同一函数调用中的
输入、输出和对象字段时，才可作为同调用链证据的一部分。

## 记录原则

- 只写实际从目标运行时读取到的值；未知字段省略或写 `null`。
- Unity `PathID`、对象实例 ID、文件路径和 GPU `ResourceId` 必须分开保存。
- 不根据名称、顺序或近似值推断材质与 Shader 关系。
- 事件文件由桥接模块负责追加，`my-hook-tool finalize` 或 `attach/mumu --watch`
  的自动收尾只负责校验 JSON、合并记录并更新会话状态。

当前 `attach` 的 Windows 注入器只验证目标模块是否成功加载。MUMU 的 Android
客体桥接仍需在客体进程中加载与 Native Bridge 宿主 ABI 匹配的模块；将 Windows
DLL 注入 `MuMuNxMain.exe` 不能自动获得客体 Unity 对象数据。当前 HSR MuMu
实例由 x86_64 Native Bridge 承载 ARM64 客体，所以使用 x86_64 客体库。

SR 分支的 `mumu` 命令会在既有 MUMU 重启流程中同时编排 Windows 宿主层和
Profile 对应的 Android 客体 Ripper，目的是让两层尽早加载。客体 Ripper 默认从
Profile 自动解析，也可用 `--guest-ripper` 覆盖，或用 `--no-guest-ripper` 明确关闭。
传入 `--launch-package <id>` 会通过 ADB 启动已安装的 Android 程序；只有在客体
Ripper 已启用时，MUMU `wrap` 才会把对应 ABI 的 `.so` 加载进该客体。它不会把
Windows 宿主 DLL 当作 Unity 资源信息。仓库内一键脚本会在 MuMu VM 仍可查询时
列出第三方包，并把用户选择的真实包名传给该参数；无法查询时不会自动猜测目标程序。

## Draw identity bridge

为了把运行时 Unity 资源和 RenderDoc 对齐，客体桥接模块可以追加
`unity.draw-context` 事件。该事件只有在桥接模块已经验证字段来源和调用上下文
时才允许使用 `status: "verified"`；未验证的数据必须使用其他事件类型，不能被
对齐器当作身份。

```json
{
  "schema": "my-hook.runtime-event.v1",
  "kind": "unity.draw-context",
  "status": "verified",
  "frameId": "frame-3630",
  "renderDocEventId": 436,
  "drawId": 17,
  "shaderPathId": -7177827563780441733,
  "shaderName": "miHoYo/CRP_Particles/Particle_OneChannel",
  "materialPathId": 4069867014352870362,
  "materialName": "Eff_None_OneChannel_22"
}
```

`renderDocEventId` 必须来自与 RenderDoc capture 共享的帧/事件标记，不能用时间、
顺序或地址代替。`shaderPathId` 和 `materialPathId` 必须是客体运行时实际读取到
的 Unity PathID；名称只能作为同一身份的附加展示字段。`my-hook-tool align` 会
校验事件 ID、Shader PathID 和 Material PathID 均存在于对应输入，校验失败的字段
不会生成链接。

当前仓库的 TypeTreeRipper 仍不会自动生成这种事件。它目前只生成 TypeTree、类型
观察和 Transfer 原始证据；需要独立的、与目标 Unity/IL2CPP 版本匹配的客体桥接
模块才能填充 `unity.draw-context`。

## 完整性判定

只有以下三段证据同时成立，分析结果才允许标记为 `verified`：

1. TypeTree 节点和实际字段值均非空且可解码。
2. 同一运行时对象事件同时给出 Material/Shader 与 `sourceFile#PathID`。
3. 同一对象在同一 RenderDoc capture 中关联到明确的 EID。

TypeTree schema、Transfer 记录、PPtr 记录或 opaque identity 记录单独存在时，
只能标记为 `observed` 或 `blocked`。
