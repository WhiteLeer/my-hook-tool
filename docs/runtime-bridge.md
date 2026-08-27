# Runtime Bridge Protocol

运行时桥接模块由目标进程加载，使用环境变量找到当前会话：

- `MY_HOOK_PROFILE`：Profile ID。
- `MY_HOOK_OUTPUT`：runtime 输出目录。
- `MY_HOOK_EVENTS`：NDJSON 事件文件的绝对路径。
- `MY_HOOK_FILE`：当前 `.hook` 文件路径。

TypeTreeRipper 输出可以在会话结束时通过 `finalize --typetree` 挂入：

```powershell
my-hook-tool.exe finalize '<session>\<session>.hook' `
  --typetree 'D:\path\to\release.ttbin'
```

文件会复制到 `.hook` 会话的 `runtime/typetree/`，并记录 TypeTree 格式、大小、
SHA-256 和原文件名。当前工具不重新解释 TypeTree 二进制；`.tpk` 或结构文本
仍保留为原始证据，转换继续使用 TypeTreeRipper 自带 Converter。

桥接模块以 UTF-8、逐行追加的方式写入 `MY_HOOK_EVENTS`。采集期间不需要工具
反复重建 `.hook`；工具只监听目标进程生命周期，结束时再读取事件文件一次。
每一行必须是一个
JSON 对象，且包含 `schema`、`kind`、`source` 和 `payload`：

```json
{
  "schema": "my-hook.runtime-event.v1",
  "kind": "material",
  "source": {
    "layer": "unity-runtime",
    "profile": "HSR-4.4-MUMU"
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

## 记录原则

- 只写实际从目标运行时读取到的值；未知字段省略或写 `null`。
- Unity `PathID`、对象实例 ID、文件路径和 GPU `ResourceId` 必须分开保存。
- 不根据名称、顺序或近似值推断材质与 Shader 关系。
- 事件文件由桥接模块负责追加，`my-hook-tool finalize` 或 `attach/mumu --watch`
  的自动收尾只负责校验 JSON、合并记录并更新会话状态。

当前 `attach` 的 Windows 注入器只验证目标模块是否成功加载。MUMU 的 Android
客体桥接仍需在客体进程中加载对应 ARM64 模块；将 Windows DLL 注入
`MuMuNxMain.exe` 不能自动获得客体 Unity 对象数据。

SR 分支的 `mumu` 命令只是在既有 MUMU 重启流程中复用 Windows 宿主注入，目的
是保证模块尽早加载。传入 `--launch-package <id>` 只会通过 ADB 请求启动已安装
的 Android 程序，不等于向该程序注入 ARM64 `.so`；它也不会把宿主进程的模块
信息当作 Unity 资源信息。仓库内一键脚本会在 MuMu VM 仍可查询时列出第三方包，
并把用户选择的真实包名传给该参数；无法查询时不会自动猜测目标程序。
