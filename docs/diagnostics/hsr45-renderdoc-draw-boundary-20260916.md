# HSR 4.5 Draw 身份桥边界

## 范围

本记录只判断当前代码是否已经提供“客体运行时对象 -> RenderDoc EID”的直接交汇点；不把会话编号、帧号、地址、顺序、`ResourceId` 或 hash 当作身份。

## confirmed

- RenderDoc 的 `WrappedVulkan::HandlePreCallback` 在回放阶段从 `m_ActionUses` 读取事件号，并在 `PreDraw`/`PostDraw` 回调中使用它。证据：`Base_RenderDoc/my-renderdoc/renderdoc/driver/vulkan/vk_core.cpp`，`HandlePreCallback`。
- `Serialise_vkCmdDraw` 和 `Serialise_vkCmdDrawIndexed` 的捕获阶段只序列化 Vulkan 命令；当前实现没有调用 Hook 运行库、写入 Hook 事件，或导出客体 Material/Shader 身份。证据：`Base_RenderDoc/my-renderdoc/renderdoc/driver/vulkan/wrappers/vk_draw_funcs.cpp`，对应 `IsCaptureMode(m_State)` 分支。
- 当前 RenderDoc 工作树的功能性修改集中在 `renderdoccmd/shader_export.cpp`、README 和启动脚本；没有捕获期 Vulkan draw 到客体 Hook 的共享回调实现。
- 当前 TypeTreeRipper 的 `HookedTransferAtOffset`、PPtr 和 serialized identity 观察都发生在 Unity 序列化/加载路径；`activeTransferCallChain` 只有在 resolver 嵌套于 Transfer 的同线程调用链中才会出现。

## observed

- HSR 4.5 联合会话确实生成了有效 `.srrdc` 和运行时 `.hook`，但运行时事件没有 `unity.draw-context`、EID 或共享 draw 标记。
- 该会话的 RenderDoc 离线导出得到真实 `eventId`；运行时返回的目标控制帧号不是 EID。
- Transfer 嵌套回归中 `activeTransferCallChainCount=0`，说明当前观察到的 resolver 路径不在已观测 Transfer 回调内部执行。

## hypothesis

- 要闭合身份桥，必须在同一客体进程内增加捕获期 Vulkan draw 观测，并让客体 Hook 记录可回放映射的原始 draw 标记；随后仍需独立证明该 draw 标记对应的 Material/Shader 运行时对象和 `sourceFile#PathID`。
- 捕获期只能天然得到命令/序列化位置，最终 EID 在 RenderDoc 回放构建 Action Tree 时才确定。因此捕获期标记不能直接伪称 EID，必须保留可验证的中间键，并在回放侧建立显式映射。

## blocked

- 当前没有同一调用链或同一对象证据，把 Material/Shader 运行时对象连接到某个 RenderDoc EID。
- 当前没有证据证明 `key64`、`runtimeValue32`、地址或任何事件顺序字段分别是 Unity `PathID`、`InstanceID` 或 RenderDoc EID。
- 在补齐捕获期 draw 观测前，修改对齐器用已有字段自动连接会违反禁止猜测约束。

## 最小实验契约

实验不得自动启动 MuMu，也不得先修改 ABI 相关 Hook。实现前必须明确记录：

1. 捕获期每个真实 `vkCmdDraw*` 的原始命令位置、命令缓冲区身份、线程身份和调用类型。
2. 回放期该原始命令位置到唯一 `eventId` 的显式映射。
3. 客体 Hook 在同一 draw 调用边界记录的运行时对象句柄，以及同一对象可读取的 Material/Shader `sourceFile#PathID`。
4. 成功标准是同一条记录同时包含上述三类证据，并可在第二次相同实验中复现；否则结果只能是 `blocked`。

