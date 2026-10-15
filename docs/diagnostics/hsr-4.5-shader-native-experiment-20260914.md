# HSR 4.5 Shader Native Experiment

## Scope

本记录只针对 HSR 4.5.0 客体、Unity 2019.4.34 Runtime 和 `ClassID=48`
（Shader）。HSR 4.4 不属于本实验目标；历史 4.4 文件不能作为 4.5 的运行时字段或 ABI 结论。

## Confirmed

- guest TypeTreeRipper 生成了 `runtime/typetree/release.ttbin`，大小 75511 bytes。
- 严格校验结果为 `complete_for_requested_types`：1 个 Shader tree、1551 个节点、字段完整性 `valid`。
- TypeTree 头部版本为 Unity `2019.4.34`、变体 `Runtime`，与 HSR 4.5 Profile 一致。
- Shader Transfer observer 安装成功，记录的 `typeId=48`、已校验 vtable 槽位和首指令均匹配 Profile。
- ARM64 guest serialized-identity raw patch 安装成功；写回扫描发现 2 个只读候选站点，并记录了 2 个同站点序列证据。
- 实验事件文件最终包含 412 条记录；其中有安装、写回扫描和桥接加载记录。

## Observed

- 没有 `unity.transfer` 实际回调记录。
- 没有 Shader 或 Material 对象快照。
- 没有 `unity.draw-context`、`sourceFile#PathID` 或 RenderDoc EID 共享身份记录。
- 没有 serialized-identity producer/resolve 实际调用记录。
- 客体在 native TypeTree 调用后退出，工具因此将会话标记为
  `failed_guest_process_exited`；该状态不能被当作运行时身份桥接成功。

证据目录：

`artifacts/HookSessions/native-shader-hsr45-r4-20260914/HSR-4.5-MUMU_20260914_081610784/`

关键文件：`runtime/events.ndjson`、`runtime/typetree/release.ttbin`、
`runtime/typetree/shader-verify.json`、`analysis.json`。

## Hypothesis

- 目前更可能是 native TypeTree 试验在 Shader 对象真正进入 Transfer 前导致客体退出，
  而不是 Shader TypeTree 数据为空或字段解析失败。
- 在没有实际 Transfer 事件前，不能判断 raw patch mailbox 中的字段是 PathID、InstanceID
  或其他运行时值，也不能据此建立 AS 与 RenderDoc 的绑定。

## Blocked

- 尚未证明 HSR 4.5 的 Shader 对象会在当前注入时机经过已校验的 Transfer 调用。
- 尚未获得同一对象在 guest identity、AS `sourceFile#PathID` 和 RenderDoc EID 三侧的直接共同证据。
- 下一实验必须先解决“客体在 native 调用后退出”这一运行时稳定性问题；在此之前不接受任何
  Material/Shader 绑定或字段语义结论。

## Safe observer control

随后进行的单变量对照实验关闭了 native TypeTree、identity raw-patch、producer 和 resolve
observer，只保留 `ClassID=48` 的 Shader Transfer observer。会话状态为
`completed_observer_only`，没有客体崩溃；但观察窗口仅产生 2 条事件：一条桥接接口记录和一条
Transfer observer 安装记录，实际 Shader Transfer 调用为 0，guest catalog 仅 4684 bytes。

这排除了“native TypeTree 调用本身是唯一原因”的解释，但不能证明 Shader Transfer ABI 无效。
下一次运行必须以“客体已进入可渲染游戏场景”为前置条件，再启用安全 observer；自动启动停在
启动/登录边界时，结果只能作为未触发负例。
