# HSR 4.5 身份映射与 Transfer 上下文实验

## 会话

- 客体：HSR 4.5.0，MUMU，`com.miHoYo.hkrpg`
- 会话：`HSR-4.5-MUMU_20260911_061242210`
- 模式：仅观察 Material/Shader Transfer、序列化身份映射和序列化上下文
- native TypeTree 生成：未启用

## 结果

本次会话生成了 `2187` 条有效运行时记录，其中：

- `1724` 条 `unity.serialized-identity-map`
- `14` 条 `unity.transfer-context-call`
- `6` 条 Material Transfer
- `2` 个不同 Material 对象

上下文回调均能记录有效的 Material Transfer 上下文。首个 Material 调用中，
上下文参数 `argument0` 与对象 `+0x28` 的低 32 位一致，`argument1` 与 Transfer
`+0x58` 的低 32 位一致；这些是同一次客体调用中的直接关系证据。

身份映射事件没有携带 `activeTransfer`。因此当前实验不能把
`keyWord0/key64/keyBytes16/runtimeValue32` 直接绑定到某个 Material 或 Shader，
也不能把 `key64` 改名为 PathID。

## 结论

“身份映射函数在 Transfer 嵌套期间执行，因此可用线程局部 Transfer 上下文关联”
这一假设已被实机数据排除。上下文观察器本身有效，但身份映射函数属于另一条
对象装载/持久化路径。

下一步必须观察身份映射调用方对返回值的实际写回对象，或找到同版本
PersistentManager 的反向查询入口；只有取得 `(runtime object, sourceFile, PathID)`
三元关系后，才能继续建立 Material/Shader 与 RenderDoc EID 的桥接。

## 产物

```text
artifacts/HookSessions/identity-context-20260911/HSR-4.5-MUMU_20260911_061242210/HSR-4.5-MUMU_20260911_061242210.hook
artifacts/HookSessions/identity-context-20260911/HSR-4.5-MUMU_20260911_061242210/identity-context.analysis.json
```
