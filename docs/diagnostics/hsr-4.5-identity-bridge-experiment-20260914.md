# HSR 4.5 身份桥最小实验规格

本规格只针对当前 HSR 4.5.0 客体，不继承 HSR 4.4 的 ABI、偏移或实验结论。

## 触发条件

- 使用 `profiles/HSR-4.5-MUMU.json`，只选一个已确认会加载的 Material ClassID。
- 保持现有安全 Transfer 观察器开启；任何 native TypeTree 调用必须显式 opt-in，且单次只测试一个 ClassID。
- 在同一进程生命周期内记录对象加载、身份函数调用、Material Transfer 和 RenderDoc 捕获事件。

## 必须记录

每条候选关联必须来自同一调用或同一对象上下文，并同时包含：

- SerializedFile/sourceFile 标识及其原始类型；
- 序列化 PathID 及其原始宽度/符号；
- runtime object handle；
- Material/Shader 类型和字段级 TypeTree 来源；
- RenderDoc captureSessionId、frameId 和 EID；
- 调用点、返回值、回写地址和 ABI 版本证据。

只有事件记录中直接出现这些字段，且能通过同一对象句柄闭合，才允许生成跨来源 link。

## 成功标准

1. 至少一个 Material 在同一加载上下文中同时具备 `sourceFile#PathID` 和 runtime object handle。
2. 该 Material 的 Shader PPtr 具有同样强度的直接关联，而不是名称、地址、顺序或数值碰撞。
3. 同一对象在 RenderDoc 捕获中带有明确共享的 frame/EID 事件身份。
4. 重复实验得到相同字段语义；否则结果只能标记为 observed，不得进入 verified。

## 当前结果

- 现有 4.5 PPtr 回归未满足任何一条跨来源成功标准：`identityBridge.status=not_observed`。
- 现有 4.5 native Material TypeTree 可作为字段树证据，但不能替代对象身份桥。
- 下一步若执行实机实验，只允许围绕上述字段采集扩展记录，不新增未经证实的 RVA 或字段解释。
