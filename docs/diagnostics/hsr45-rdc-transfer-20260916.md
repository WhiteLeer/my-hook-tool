# HSR 4.5 同会话 Transfer/RenderDoc 实验

## confirmed

- 客体版本为 HSR `4.5.0`，Unity 运行时 PID 为 `2378`，客体 Ripper 使用已校验的 `libTypeTreeRipper.so`。
- 同一 Hook 会话成功启动自定义 RenderDoc，并完成一次真实捕获：
  `artifacts/HookSessions/hsr45-rdc-transfer-20260916/HSR-4.5-MUMU_20260916_044613967/capture_frame10930.srrdc`
- 同一会话的运行时事件包含 3 条 `unity.transfer`，全部是 `typeId=21`、`typeName=Material`，并有真实 `object`、`transfer`、线程号和调用者证据。
- 运行时事件文件共 76 条记录，JSON Lines 可完整解析；3 条 Material 快照均可读，`invalidSnapshots=0`。
- TypeTree 目录成功生成；当前目录包含 `Material`/`Shader` 的离线基线验证数据，但不是本次客体运行时生成的字段树。

## observed

- Transfer 记录可以证明 Unity 的 Material Transfer 回调已经被调用，但记录状态仍为 `observed_not_decoded`。
- 现有 RenderDoc 捕获可以证明同一会话产生了 GPU 帧，但 Hook 事件中没有 `renderDocEventId`、Material `sourceFile#PathID`、Shader `sourceFile#PathID` 或 `activeTransferCallChain`。
- 当前 RenderDoc 侧只有捕获文件；没有把每个 EID 的资源绑定信息写入 Hook 事件的同对象记录。

## hypothesis

- 继续增加 Material 快照字节或扩大 TypeTree 基线，不能自行建立 EID 关联；缺少的是同一 Draw/提交路径中的对象身份观测，而不是数据量不足。
- 需要在 RenderDoc 的同一捕获会话中记录一个可验证的提交边界，并由客体侧在同一边界输出 Material/Shader 的序列化身份；两者必须通过同一调用链或显式捕获序列关联。

## blocked

- 不能把 `object` 地址、`transfer` 地址、事件顺序、RenderDoc `ResourceId` 或 Shader 名称用作跨来源匹配键。
- 当前证据不足以把任意 Material/Shader 映射到 RenderDoc EID，也不足以证明 `key64` 或 `runtimeValue32` 是 `PathID`/`InstanceID`。
- 下一实验应只增加一个观测点：在已成功的 RenderDoc 捕获触发路径上记录 Draw/提交边界与运行时 Transfer 的同会话序列号；若仍无同对象字段，则应保持 `blocked`，不修改解析语义。

## HSR 4.5 QueueSubmit2 复核（2026-09-16）

### confirmed

- 在 `hsr45-queue-submit2-game-20260916` 和 `hsr45-queue-submit2-game4-20260916` 会话中，HSR 主进程均报告 `vkQueueSubmit2` Hook 安装成功，`dobbyStatus=0`，首指令校验值为 `0x48078b48`。
- 同一主进程同时报告 3 条 `unity.transfer` Material 事件，说明本次客体 Ripper 已加载且 Transfer 观察器工作。
- `hsr45-queue-submit2-game4-20260916` 的观察窗口结束后，`guest.vulkan-queue-submit` 回调仍为 0。

### observed

- MuMu 截图仍停留在 HSR 登录页的“点击进入”界面，自动点击没有形成进入游戏场景的证据；因此本轮没有真实场景绘制状态。
- 一次重启曾因同时存在 `127.0.0.1:16384` 和 `emulator-5554` 而无法自动选择 ADB 设备；显式指定 `--adb-serial 127.0.0.1:16384` 后，客体注入和 Material Transfer 恢复正常。

### hypothesis

- 当前 `vkQueueSubmit2` 结果不能判定为“函数未被游戏使用”，因为还没有完成登录并进入实际场景；更可能的直接原因是实验停留在登录页。
- 如果在确认进入场景后仍为 0，下一步应检查 Vulkan 设备函数表返回的函数指针，而不是继续重复对 `libvulkan.so` 导出符号安装 Hook。

### blocked

- 目前仍没有客体 Vulkan 提交边界与 RenderDoc EID 的同对象证据；不能生成 Material/Shader 到 EID 的精确桥接。
- 进入场景前不启动下一种函数 Hook 实验；成功标准是：同一主进程、已确认非登录 Activity、出现至少一条实际提交回调，并能与同一 RenderDoc 捕获序列建立直接调用链证据。

## Resolved Vulkan 函数指针观察路径（2026-09-16）

### confirmed

- 已在 TypeTreeRipper 中增加默认关闭的 resolved-function 路径：当 Vulkan loader 的 `vkGetDeviceProcAddr` 或 `vkGetInstanceProcAddr` 返回配置目标函数时，才允许对该返回指针做首指令校验和可选 Hook。
- resolved 路径使用独立的原函数指针和事件类型，不会覆盖导出符号 Hook 的原函数，也不会把导出地址与设备函数地址混为一谈。
- 导出入口符号与 resolved 目标符号已拆分配置：前者使用 `SR_GUEST_VK_DRAW_SYMBOL`，后者使用 `SR_GUEST_VK_RESOLVED_SYMBOL`；因此可以明确表达“Hook `vkGetDeviceProcAddr`，追踪其返回的 `vkCmdDrawIndexed`”。
- 必须同时满足 `SR_GUEST_VK_RESOLVED_HOOK=1`、目标符号匹配以及 `SR_GUEST_VK_RESOLVED_FIRST_INSTRUCTION` 校验；任一条件缺失都会记录 `skipped`。
- x86_64、ARM64 Native Runtime 和 .NET 8 工具均已重新构建，构建结果为 0 警告、0 错误。

### observed

- 既有 HSR 4.5 场景会话中，导出符号 `vkCmdDrawIndexed`/`vkGetDeviceProcAddr` 曾报告安装成功，但没有对应调用回调；这支持继续检查 loader 返回的设备函数指针，而不是继续重复导出符号 Hook。

### hypothesis

- 如果 Unity 通过 Vulkan dispatch table 使用设备级函数，resolved 路径有机会在同一客体调用链中观察到实际 Draw/Submit；这只是待验证的调用路径假设。

### blocked

- 当前尚未观察到同一 HSR 4.5 场景中真实的 resolved 函数请求和回调，因此尚未证明该路径有效。
- 即使 resolved Draw 回调成功，也只能闭合客体绘制边界；仍需将同一 Transfer 对象与 RenderDoc 的捕获序列/EID 建立直接证据后，才能输出合法身份映射。

## ARM64 companion 加载复核（2026-09-16）

### confirmed

- `events.ndjson` 中出现 `runtime-build` 且 `architecture=arm64`，说明通过 NativeBridge 加载的 ARM64 companion 确实执行了构造入口。
- 同一事件流出现 `native-bridge-extra-load`，路径为 `/data/local/tmp/my-hook-typetree-ripper-arm64.so`，触发点为 HSR 的 `libil2cpp.so` 加载。
- 当前阻塞不是“ARM64 companion 未加载”，而是 companion 加载后没有产出 Vulkan loader 请求、resolved function callback 或同调用链身份记录。

### observed

- 同一会话中已有 x86_64 侧 `guest.vulkan-draw-observer` 安装/失败记录，但没有 ARM64 侧对应的 `guest.vulkan-draw-poller-started`、`guest.vulkan-device-proc` 或 resolved callback 记录。
- 本轮增加了只读启动诊断：ARM64 companion 会记录 Vulkan poller 是否启动，以及 runtime-build 中的 Vulkan 开关状态；不改变 Hook 安装逻辑。

### hypothesis

- ARM64 companion 可能在 Vulkan loader 可见前启动、解析了不同命名空间，或其事件写入路径与主库不同；必须先由新增诊断区分这三种情况。

### blocked

- 在出现 ARM64 poller 启动和真实 Vulkan 函数请求之前，不接受任何 x86_64 导出地址或首指令作为 guest Vulkan ABI 证据。
- 最终 `sourceFile#PathID -> runtime object -> Material/Shader -> RenderDoc EID` 链仍未闭合。

## ARM64 Vulkan loader 实验（2026-09-16）

### confirmed

- 会话 `hsr45-arm64-vulkan-device-hook-20260916-r2` 中，ARM64 进程对 `vkGetDeviceProcAddr` 的 Hook 安装成功：`dobbyStatus=0`，实测首指令为 `0xbe9`。
- 会话 `hsr45-arm64-vulkan-instance-hook-20260916-r2` 中，ARM64 进程对 `vkGetInstanceProcAddr` 的 Hook 安装成功：`dobbyStatus=0`，实测首指令为 `0xbe9`。
- 两个会话均包含 `runtime-build architecture=arm64` 和 `native-bridge-extra-load`，因此本次安装结果来自 ARM64 companion，而非把宿主 x86_64 地址当作客体证据。
- 两个会话均没有 `guest.vulkan-device-proc`、`guest.vulkan-instance-proc` 或 resolved draw/submit 回调。

### observed

- 两个 loader Hook 会话各有 2 条 `installed` 记录，另有 400 条 `no-executable-symbol` 重试记录；后者来自其他进程/映射，不应与已安装的 ARM64 进程混为一谈。
- `hsr45-arm64-vulkan-create-instance-probe-20260916-r1` 只读探测得到 `vkCreateInstance` 首指令 `0xff734be9`，但没有进入安装阶段。
- 对 `vkCreateInstance` 的安装实验在产生任何回调前触发客体 `SIGABRT`，日志为 `bionic_allocator.cpp:116 alloc CHECK`；该实验已停止，新增的 ABI 假设代码已剥离并重新构建 ARM64 companion。

### hypothesis

- 当前 HSR 的实际 Vulkan 调用可能绕过 `libvulkan.so` 的这些导出入口，或在 companion 完成安装前已经完成了所需的函数解析；目前不能仅凭“安装成功”选择其中一种解释。
- 下一条实验应优先观察已解析的设备函数表/真实调用链，而不是继续增加导出符号 Hook。

### blocked

- 还没有一次真实 Vulkan 调用回调，因此没有可接受的 Draw/Submit 边界，更没有 Material/Shader 到 RenderDoc EID 的同对象关联。
- `vkCreateInstance` 的失败实验不能证明其函数语义或 ABI，只能作为“当前安装方式可能破坏进程”的负面证据。
- 在获得真实回调及同一次调用中的 Transfer 上下文前，禁止生成 `sourceFile#PathID`、Material/Shader 或 EID 的推测映射。

### evidence

- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-arm64-vulkan-device-hook-20260916-r2/HSR-4.5-MUMU_20260916_063511475/runtime/events.ndjson`
  - `guest.vulkan-draw-observer status=installed`, `symbol=vkGetDeviceProcAddr`, `firstInstruction=0xbe9`
  - `runtime-build architecture=arm64`, `native-bridge-extra-load`
- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-arm64-vulkan-instance-hook-20260916-r2/HSR-4.5-MUMU_20260916_064213218/runtime/events.ndjson`
  - `guest.vulkan-draw-observer status=installed`, `symbol=vkGetInstanceProcAddr`, `firstInstruction=0xbe9`
- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-arm64-vulkan-create-instance-hook-20260916-r1/HSR-4.5-MUMU_20260916_065036932/runtime/typetree/android-errors-logcat.txt`
  - `SIGABRT` 与 `bionic_allocator.cpp:116`，且事件流没有 `guest.vulkan-create-instance`

## ARM64 queue-submit 实际场景复核与间接槽实验（2026-09-16）

### confirmed

- `hsr45-arm64-vulkan-queue-submit-early-hook-20260916-r2` 在 HSR 4.5 实际场景中加载 ARM64 companion，并对 `vkQueueSubmit` 报告 `dobbyStatus=0`、首指令 `0x48078b48`。
- 同一会话没有 `guest.vulkan-queue-submit` 回调；“导出入口已安装”与“实际渲染路径调用该入口”已被明确区分。
- 显式开启的单级间接槽实验读取到的 `indirectTarget=0x6420676e69737369` 不在可执行映射中，未安装该候选。

### observed

- 间接实验的 `dobbyTarget` 位于 `/system/lib64/libvulkan.so` 的 `r--p` 映射，`dlsymTarget` 也没有形成可执行映射；事件流多次出现 `no-executable-symbol`。
- 该间接候选只能记录为无效候选，不能解释为 Vulkan dispatch table 或真实函数。

### hypothesis

- 当前 loader 导出结果可能是数据槽、占位值或 NativeBridge 命名空间中的非代码地址；单级解引用不足以证明设备函数表关系。
- HSR 的有效 Vulkan 调用边界仍需从真实设备函数解析或更上游、已验证的调用链获得。

### blocked

- 仍未观察到真实 guest Vulkan Submit/Draw 回调，因此不能把 Vulkan 时间点与 RenderDoc EID 对齐。
- TypeTree 本轮仍只有 class catalog；`release.ttbin` 为无字段树，不足以证明 Material/Shader 的字段级运行时 TypeTree。
- `sourceFile#PathID -> runtime object -> Material/Shader -> RenderDoc EID` 仍未闭合；不接受地址、顺序、名称、ResourceId、key64 或 runtimeValue32 推断。

### evidence

- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-arm64-vulkan-queue-submit-early-hook-20260916-r2/HSR-4.5-MUMU_20260916_112536019/runtime/events.ndjson`：ARM64 load/install evidence，`guest.vulkan-queue-submit` 计数为 0。
- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-arm64-vulkan-queue-submit-indirect-20260916-r1/HSR-4.5-MUMU_20260916_113922199/runtime/events.ndjson`：`indirectTarget`、`targetMap.permissions=r--p`、`no-executable-symbol`，无实际 Submit 回调。
- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-arm64-vulkan-queue-submit-indirect-20260916-r1/HSR-4.5-MUMU_20260916_113922199/runtime/typetree/release.ttbin`：仅有 Runtime 头部、无字段树内容。

## ELF 符号解析复核（2026-09-16）

### confirmed

- 从当前 MuMu 系统拉取的 `/system/lib64/libvulkan.so` 中，`vkQueueSubmit` 是 `STT_FUNC`，ELF 符号值为 `0x1d6b0`；`vkGetDeviceProcAddr`、`vkGetInstanceProcAddr` 同样是 `STT_FUNC`。
- 新增的 ARM64/x86_64 ELF 动态符号解析只接受 `STT_FUNC` 且落在 `r-xp` 映射内的地址；它不改变默认间接实验开关，也不放宽完整性校验。
- `hsr45-arm64-vulkan-queue-submit-elf-probe-20260916-r1` 在 ARM64 companion 中观察到 6 条 `vkQueueSubmit` 探针，均为 `firstInstruction=0x48078b48`、目标映射为 `/system/lib64/libvulkan.so` 的 `r-xp` 段；同会话有 `runtime-build architecture=arm64` 和 `native-bridge-extra-load`。

### observed

- 该 ELF 探针是只读探针，不安装 Hook，因此不能证明实际 Submit 调用发生。
- 之前 Dobby/dlsym 的候选中存在 `r--p` 映射和不可执行值；这解释了为什么不能直接把 resolver 返回值当成函数入口。

### hypothesis

- ELF 符号解析修复了“候选地址来源不一致”的工具问题，但不改变 HSR 可能绕过导出入口、使用设备分发表的待验证假设。

### blocked

- 还没有在 ELF 精确解析后的正式 Hook 中观察到真实 Submit/Draw 回调；下一实验必须在已进入游戏场景后进行正式 Hook，而不是继续用名称或地址猜测。
- TypeTree 字段级输出和 `sourceFile#PathID -> Material/Shader -> RenderDoc EID` 同对象证据仍未闭合。

## ELF 正式 Hook 安全复核（2026-09-16）

### confirmed

- `hsr45-arm64-vulkan-queue-submit-elf-hook-20260916-r3` 确实启动了 HSR 4.5 进程，并加载了 ARM64 companion；这不是“只停留在启动器”的有效性证明。
- 该会话随后在 Unity 进程内发生 `SIGSEGV`，回溯落在 `my-hook-typetree-ripper-arm64.so` 的 `_clear_cache`，而不是产生真实 `vkQueueSubmit` 回调。
- 因此“ELF 符号解析正确”与“对 NativeBridge/guest Vulkan 入口做内联 Hook 安全且可调用”是两个独立命题；前者已由只读探针支持，后者当前被实机崩溃否定。

### observed

- 崩溃证据：`runtime/typetree/android-errors-logcat.txt`，HSR 进程 `com.miHoYo.hkrpg`，Unity `2019.4.34f1`，`SIGSEGV`；回溯包含 `_clear_cache` 和匿名 trampoline。
- 同一会话的 `events.ndjson` 只有 `guest.vulkan-draw-observer status=skipped/no-executable-symbol` 与安装前后的基础事件，没有 `guest.vulkan-queue-submit`。
- 当前静态 `libunity.so` 扫描仍只能给出 TypeTree 字符串引用与有界 `BL` 候选，工具报告明确标记为 candidate，不是已确认 ABI 或字段生成入口。

### hypothesis

- 崩溃更符合 NativeBridge 下对导出入口/翻译代码做 Dobby/inline patch 的兼容性问题，不能据此推断 HSR 的实际设备分发表路径。
- 下一阶段应优先验证 Unity 对象加载/反序列化边界中已存在的对象上下文，再决定是否需要更低层的图形边界；不应继续叠加 `vkQueueSubmit`、`vkCreateInstance` 等导出 Hook 变体。

### blocked

- 当前没有安全、可重复的真实 Draw/Submit 回调；Vulkan 导出 Hook 路线暂时停止。
- 没有字段级运行时 TypeTree，也没有同一次调用内的 `sourceFile#PathID -> Material/Shader -> RenderDoc EID` 证据。
- 在获得上述同对象证据前，不接受地址、调用顺序、Shader 名称、ResourceId、key64 或 runtimeValue32 作为身份桥。

### evidence

- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-arm64-vulkan-queue-submit-elf-hook-20260916-r3/HSR-4.5-MUMU_20260916_120920517/runtime/typetree/android-errors-logcat.txt`
- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-arm64-vulkan-queue-submit-elf-hook-20260916-r3/HSR-4.5-MUMU_20260916_120920517/runtime/events.ndjson`
- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/analysis/HSR-4.5/libunity-typetree-static-20260916.json`

## Unity 对象加载与消费者同对象关联（2026-09-16）

### confirmed

- r3 会话在 `unity.serialized-object-load-boundary` 与 `unity.serialized-object-consumer` 中观察到 1024 对相同 `processId + threadId + returnObjectCandidate/objectCandidate` 的记录。
- 这些记录来自同一组已校验的 caller：加载边界 caller 为 `0x4000235ac538`，消费者 caller 为 `0x4000235ac580`；这只能证明两个观测点看到了同一 native object candidate，不能把该对象解释为 Material、Shader 或某个 PathID。
- 加载边界记录同时给出了 `manager`、`typeInfo`、`output`、`flags`、`mode`、`options` 与返回对象候选；消费者记录给出了 `context`、`keyRecord`、输出槽位前后值和对象 vtable 可读性。它们是下一步字段级实验的真实输入。

### observed

- r3 的事件分布为：`unity.serialized-object-load-boundary=4096`、`unity.serialized-object-consumer=1024`、`unity.serialized-pptr-candidate=48536`。
- 加载边界事件的 `keyOpaque64` 在样本中既有 `0x0` 也有非零值，但当前证据没有定义其 ABI 语义；不能将它当作 PathID、FileID 或 InstanceID。
- `serialized-pptr-candidate` 中的 `keyBytes16`、`argument1`、`outputAddress` 与 `result` 也没有同一调用内的 sourceFile/PathID 证明。

### hypothesis

- 已闭合的对象候选关联说明 Unity 反序列化/对象消费边界比 Vulkan 导出层更接近最终身份桥；下一实验应在相同回调链内记录原始参数、序列化上下文和字段读取，而不是扩大观察对象种类。

### blocked

- 没有字段级运行时 TypeTree；`typeInfo` 仍只是地址证据，不能解码字段。
- 没有 `sourceFile#PathID`，也没有 Material/Shader 对象和 RenderDoc EID 的同调用关联。
- 1024 对同对象候选不能转化为身份映射；地址、顺序、key64 与 vtable 都只保留为审计字段。

### evidence

- `D:/Unpack_Workspace/Tools/my-hook-tool/artifacts/HookSessions/hsr45-arm64-vulkan-queue-submit-elf-hook-20260916-r3/HSR-4.5-MUMU_20260916_120920517/runtime/events.ndjson`：事件行 244 起为对象加载/消费者/PPTR 记录；可按 `processId + threadId + returnObjectCandidate/objectCandidate` 重复得到 1024 对。
