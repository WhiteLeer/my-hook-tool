# HSR 4.5 Identity and Shader Scene Experiment

## Scope

本实验只使用 HSR 4.5.0 客体和 `HSR-4.5-MUMU` Profile。实验在标题页点击“点击进入”后
继续采集，避免把 SDK 登录页误认为游戏场景。

## Confirmed

- 同一会话成功安装 Shader Transfer observer 和 ARM64 guest serialized-identity raw patch。
- 最终事件文件包含 65075 条记录：158 条 `unity.transfer`、58736 条
  `unity.serialized-identity-map`、5722 条 `unity.serialized-identity-writeback`。
- 158 条 Transfer 全部是 `ClassID=48 / Shader`；分析器识别出 79 个不同运行时对象，
  158 个快照均通过完整性校验，并命中已观测的 Shader 引用。
- identity map 的 key、返回值和延迟 writeback 均能稳定记录；同一 record 的返回值与写回值
  在 2148 个匹配 record 中有 1940 次数值一致。这证明了调用返回值确实进入了调用者的写回路径。

## Observed

- identity map 事件没有 `activeTypeId` 或同一 Transfer 上下文。
- 事件没有 `sourceFile`、`PathID`、Material/Shader 序列化 PPtr、或 RenderDoc EID。
- 当前 identity 目标和 Shader Transfer 目标都是真实调用点，但两者尚未在同一调用上下文中闭合。
- 仅启用 Shader Transfer 的安全对照会话可获得 79 次 Shader Transfer；因此之前的 0 次是标题页
  未进入场景造成的，不是 Transfer observer 安装失败。

证据目录：

`artifacts/HookSessions/hsr45-identity-scene-20260914/HSR-4.5-MUMU_20260914_083614860/`

关键文件：`runtime/events.ndjson`、`analysis.json`、对应 `.hook`。

## Hypothesis

- `0x23befb0` 更像独立的内部序列化身份索引操作；它不一定在 Shader Transfer 的调用栈内执行。
- 要闭合 AS `sourceFile#PathID`，下一候选应是已由静态证据定位的 PPtr/SerializedFile 读取边界，
  但必须先完成参数 ABI 的只读验证，不能直接把其 32/64 位参数命名为 PathID 或 InstanceID。

## Blocked

- 仍没有同一对象的 `sourceFile + PathID + runtime object handle` 三元直接证据。
- Material/Shader 与 RenderDoc EID 的关系仍未证明；当前不能用 Shader 对象地址、事件顺序或
  ResourceId 代替该关系。
- 下一步需要一个新的、版本化的只读 PPtr/SerializedFile 边界实验；在该 ABI 被验证前不应修改
  identity 字段命名，也不应导出绑定结果。
