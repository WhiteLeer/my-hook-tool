# HSR 4.5 身份调用点审计

## confirmed

- 会话：`HSR-4.5-MUMU_20260914_041040829`。
- `unity.serialized-identity-map` 共 1,724 条；其中 1,723 条的 `caller` 为
  `0x4000233ad198`，另 1 条为 `0x4000233ad178`。
- 观察到两个稳定的调用序列：调用点分别为
  `0x4000233ad174`、`0x4000233ad194`；对应写回位置分别为
  `0x4000233ad17c`、`0x4000233ad19c`。
- 证据文件：
  `artifacts/HookSessions/native-material-hsr45/HSR-4.5-MUMU_20260914_041040829/runtime/events.ndjson`。

## observed

- 该会话的身份事件没有 `sourceFile`、`PathID`、Material/Shader 句柄或
  RenderDoc EID 字段。
- 所有身份调用均没有 `activeTransfer` 上下文；分析结果仍为
  `identityBridge.status=not_observed`。
- 写回位置的稳定性只证明调用者代码路径稳定，不证明输入 key、返回值或写回记录的字段语义。

## hypothesis

- 这些位置可能属于运行时身份查找及其结果写回，但当前证据不足以确定它们是否连接到
  SerializedFile、PersistentManager 或 Unity 对象句柄。

## blocked

- 不能据此生成 `sourceFile#PathID -> runtime object`，也不能据此绑定 Material/Shader
  到 RenderDoc EID。
- 下一步必须在同一对象、同一加载/解析调用中直接记录序列化身份与 runtime handle；不得使用
  地址、顺序、名称、计数、ResourceId、hash 或数值碰撞补链。
