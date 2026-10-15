# HSR 4.5 单 Material 身份实验

## confirmed

- 实验会话：`HSR-4.5-MUMU_20260914_041040829`。
- HSR 4.5.0 客体完成运行，未发生崩溃；native TypeTree 生成路径保持可用。
- 运行时 TypeTree 包含 Material 树，严格解码为 76 个字段节点，字段完整性为 valid。
- 身份观察器产生 1,724 条 `unity.serialized-identity-map` 和 529 条 writeback 记录。

## observed

- 1,724 条 identity map 的 key 都可读，但没有一条带有 `activeTransfer` 上下文。
- 本次事件中 `sourceFile`、`PathID`、`materialPathId`、`shaderPathId` 和 `renderDocEventId` 均未出现。
- 分析结果为 `identityBridge.status=not_observed`，`drawContextCount=0`，Material/Shader 完整身份数均为 0。

## hypothesis

- 当前身份函数调用发生在 Transfer 回调之外；线程局部 Transfer 上下文不能作为二者的共同身份来源。
- 可读 key、返回值和延迟回写匹配只证明函数活动及调用者写回，不证明其是 PathID、InstanceID 或 SerializedFile index。

## blocked

- `sourceFile#PathID -> runtime object` 尚未闭合，Material/Shader 到 RenderDoc EID 也未闭合。
- 下一实验应定位身份函数调用者的共同上层或实际对象加载回写点，并在同一调用上下文直接记录序列化身份和 runtime handle；不得继续用线程、地址、顺序或数值碰撞关联。
