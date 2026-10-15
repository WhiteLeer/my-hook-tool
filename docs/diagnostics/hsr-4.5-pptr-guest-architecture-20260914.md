# HSR 4.5 PPtr 观察器：客体架构边界

## 结论

当前不能从宿主 x86_64 注入库直接改写 HSR 4.5 的 ARM64 `libunity.so`。该路径已在 MuMu 中复现为客体崩溃，不能作为可用的 PPtr 观察实现。

正确的实验边界是：

- 宿主 `libTypeTreeRipper.so` 只做 TypeTree/事件协调，并明确拒绝 `arm64-guest` raw patch；
- ARM64 `libTypeTreeRipper.arm64.so` 通过 MuMu Native Bridge 加载，在客体架构内使用 Dobby 安装观察器；
- 只有 ARM64 库产生的调用事件才可作为客体 PPtr 观察证据。

## 证据分类

### confirmed

- HSR 4.5 客体 Unity 库是 ARM64 映像，宿主注入库是 x86_64。
- 旧实验 `hsr45-pptr-guard-20260914` 的安装事件记录了 `rva=0x23a94bc`、首指令校验通过、`rawPatch=installed`。
- 同一实验的 logcat 在客体 `UnityMain` 线程报告 `SIGSEGV`，故障地址为 `0xdead1007`，PC 等于 `rawPatchHandler` 地址。
- 崩溃回溯从匿名 handler 映射进入 `libunity.so`，证明错误发生在跨架构 handler 跳转路径，而不是“目标函数未调用”的结论。
- TypeTreeRipper 已有 ARM64 Native Bridge 探针加载链；历史会话 `guest-arm64-identity-auto-20260909` 曾产生 1724 条客体侧身份函数调用事件。
- 当前代码已让宿主 x86_64 库对 `arm64-guest` 输出 `unsupported-cross-architecture` / `refused-host-to-guest`，不再申请宿主侧 raw trampoline。
- 当前代码会在启用 `serializedPPtrObserver` 时自动请求 ARM64 companion library，并由 ARM64 构建走 Dobby 分支。

### observed

- 旧的 `hsr45-pptr-guest-dobby-20260914-r2` 曾受 MuMu ADB 启动问题影响；该结论不代表后续 ARM64 观察器回归失败。
- 受控回归 `hsr45-pptr-identity-scene-20260914-r2` 已在线完成，事件文件共 68,237 条：`unity.serialized-identity-map=1,724`、`unity.serialized-identity-writeback=532`、`unity.serialized-pptr-candidate=65,536`。
- 同一回归中，宿主 x86_64 的 identity/PPtr 路径均明确输出 `unsupported-cross-architecture` 和 `refused-host-to-guest`；ARM64 companion 的两个 Dobby 安装事件均报告 `status=installed`、`dobbyStatus=0`。
- 该回归未产生 `unity.transfer`（实际 Transfer 为 0），只有 Transfer observer 安装事件；因此这些 identity/PPtr 记录尚未与 Material/Shader 对象快照或 RenderDoc EID 建立直接关系。
- 后续运行时映射重试出现首指令不匹配，但不覆盖早期已成功安装的 ARM64 事件；本次进程无崩溃。

### hypothesis

- identity 观察器与 PPtr 观察器可能位于不同的运行时边界；必须在同一次实际 Transfer 或同一对象调用链中记录共享上下文，才能验证它们是否描述同一资源。

### blocked

- `key64`、`runtimeValue32`、地址、调用顺序、低位匹配和 RenderDoc `ResourceId/EID` 仍不能解释为 Unity `sourceFile#PathID` 或对象身份。
- 当前回归没有实际 Transfer，也没有 Material/Shader 的同对象调用上下文，因此不能闭合资源身份桥。
- 仍缺少同一对象的 `sourceFile + PathID + runtime handle` 直接记录，以及它与 RenderDoc EID 的同帧证据。

## 下一实验

下一轮不再重复 PPtr-only 回归，而是使用同一 HSR 4.5 场景开启受控的 native Transfer 观察：

1. 保持 ARM64 companion 路径，不允许宿主 x86_64 raw patch。
2. 限定一个已知 Unity ClassID，记录 Transfer 的对象快照、调用线程、调用者和 identity/PPtr 观察上下文。
3. 只在同一调用链或明确共享的对象句柄上建立关联；禁止按名称、地址、顺序、计数、低位或哈希匹配。
4. 成功标准是出现同一次 Transfer 中可复核的对象句柄与源序列化身份字段；若只有独立事件，结论仍为“桥接未证实”。

本轮启动证据：

- `D:/Unpack_Workspace/Games_MUMU/MuMuPlayerGlobal/vms/MuMuPlayerGlobal-15.0-0/logs/shell.log`
- `D:/Unpack_Workspace/Games_MUMU/MuMuPlayerGlobal/vms/MuMuPlayerGlobal-15.0-0/logs/VBox.log`
