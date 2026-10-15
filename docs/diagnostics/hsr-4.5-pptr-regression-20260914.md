# HSR 4.5 PPtr 读取链回归

## confirmed

- 分析工具成功读取大样本会话：`artifacts/HookSessions/pptr-read-chain-20260914-r1`。
- 输入包含 67,898 条运行时事件；分析结果为 3 个对象、2 个 Transfer、2 个 vtable、6 个快照引用命中。
- 分析输出中的 `identityBridge.status` 为 `not_observed`，`drawContextCount`、`completeShaderIdentityCount`、`completeMaterialIdentityCount` 和 `sharedRenderDocEventIdCount` 均为 0。

## observed

- 当前观察器能证明 Hook 安装和有限的 Transfer/内存快照路径，但没有产生 `sourceFile`、`PathID`、Material/Shader 运行时身份或 RenderDoc EID。
- `typeTreeObservations` 为空；现有非空字段级 TypeTree来自已保存的 4.5 原生实验产物，不是这次 PPtr 读取链回调解码出来的结果。

## hypothesis

- 当前 PPtr 读取链位于身份值读取或回写附近，但没有证据证明它就是 SerializedFile/PersistentManager 的对象加载边界；不能把读取到的 32/64 位值解释为 PathID 或 InstanceID。

## blocked

- 身份桥仍未闭合。下一次实验只有在同一对象、同一加载调用中同时记录 `sourceFile + PathID + runtime object handle` 时才算有效；随后还必须通过同一帧的显式 RenderDoc 事件字段建立 EID 关联。
- 不得用名称、地址、顺序、低位匹配、ResourceId 或哈希替代上述证据。
