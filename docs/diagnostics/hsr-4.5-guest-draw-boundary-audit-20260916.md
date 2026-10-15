# HSR 4.5 Guest Draw Boundary Audit

## 结论

当前 TypeTreeRipper 的既有运行时代码集中在 `libunity.so` 的 Transfer、序列化对象和 PPtr 候选路径，尚未有实机绘制观测证据。已新增一个默认关闭的 `vkCmdDrawIndexed` 探针，但它尚未用 HSR 4.5 的真实首指令配置运行验证。

## confirmed

- RenderDoc 回放侧已经独立产生 `eventId -> APIEvent.fileOffset/chunkIndex` 的严格证据。
- TypeTreeRipper 的 Material Transfer 记录可以携带运行时对象和线程上下文，但没有绘制提交标记。
- 新增探针只在显式开启且首指令匹配时安装；它只记录不透明绘制参数和同一线程当时的 Transfer 上下文。
- 在探针产生实机事件前，仍不能从现有事件生成合法的 `unity.draw-context`。

## observed

- 相关源码：`D:/Unpack_Workspace/References/TypeTreeRipper/source/android/android_impl.cpp`。
- 相关 RenderDoc 证据：`D:/Unpack_Workspace/Base_RenderDoc/my-renderdoc/x64/Development/m1-full-evidence-test-20260916/draw-replay-evidence.ndjson`。
- 当前运行库已包含 `libvulkan`/`vkCmdDrawIndexed` 探针字符串，但尚未证明 MuMu 实际调用该导出符号。
- `probe-only` 模式可以在不安装 hook 的情况下记录目标符号的实际首指令，作为后续安装校验输入。

## hypothesis

- 如果 MuMu 实际调用该导出符号，并在同一线程调用链中保留 Material 上下文，才可能建立 guest 绘制边界。
- 该假设不能通过 `dlsym`、线程号、地址、日志顺序或 ResourceId 直接成立，必须先完成版本/ABI 校验。

## blocked

- 不能把现有 Material Transfer 记录映射到 RenderDoc EID。
- 不能仅凭 RenderDoc 的 replay `fileOffset` 反推出 Unity Material/Shader 身份。
- 在获得一个真实、版本校验过的 guest draw 提交边界前，禁止写入或接受 `unity.draw-context`。

## 下一门槛

用真实 HSR 4.5 运行时解析出的首指令开启现有探针，检查它是否实际产生绘制事件，以及这些事件是否带有活动 Transfer 上下文。实验必须证明 Material/Shader 对象与该绘制提交来自同一调用链，然后才进入 RenderDoc EID 对齐。

## 实机进展

- 2026-09-16：`probe-only` 已在 `/system/lib64/libvulkan.so` 中观察到两个符号地址；只有 `r-xp` 映射中的地址被视为可执行候选，`r--p` 地址被拒绝。
- 2026-09-16：按已观察首指令安装 `vkCmdDrawIndexed` hook 成功（Dobby status 0），但同一会话没有产生 `guest.vulkan-draw-indexed` 调用记录。安装成功不等于目标函数被调用。
- 2026-09-16：`vkCmdDrawIndexedIndirect` 的 probe 也解析到同一 Vulkan 库的 `r-xp` 地址；后续必须使用其独立的四参数 ABI 验证调用，不能复用 indexed 的 handler。本轮因客体未在等待窗口内进入前台而未安装，结果不作为正负调用证据。
- 相关会话：`artifacts/HookSessions/hsr45-guest-draw-probe-20260916-r5`、`artifacts/HookSessions/hsr45-guest-draw-hook-20260916-r1`。
- 2026-09-16：`vkGetDeviceProcAddr` 的 probe 观察到 `/system/lib64/libvulkan.so` 的 `r-xp` 地址，按实测首指令 `0xbe9` 安装 hook 成功；但该会话的前台 Activity 是 `com.mihoyo.combosdk.ComboSDKActivity`，没有产生 `guest.vulkan-device-proc` 或 draw 调用记录。因此这次只证明安装链和首指令校验，不证明游戏渲染调用链。
- 对应会话：`artifacts/HookSessions/hsr45-vk-device-proc-hook-20260916-r1`。
