# HSR 4.5 生产者探针实验

## confirmed

- 会话：`HSR-4.5-MUMU_20260914_043832144`。
- 生产者目标 `0x23a87a0` 通过首指令 `0xf81a0ffe` 校验并安装成功。
- 产生 49 条 `unity.serialized-identity-producer-call` 事件；原函数正常返回，
  客体未崩溃。
- 最新运行时 TypeTree 仍成功生成：`Material`，76 个节点，字段完整性 `valid`。
- 证据文件：
  `artifacts/HookSessions/native-material-hsr45/HSR-4.5-MUMU_20260914_043832144/runtime/events.ndjson`。

## observed

- 生产者事件的 `callerPc` 分为 3 个值：
  - `0x4000228a98d0`：40 次
  - `0x400023143fe8`：8 次
  - `0x4000233acf24`：1 次
- 四个参数均出现非空地址；受限快照中可见指针样值和计数样值，但事件没有
  `sourceFile`、`PathID`、Material/Shader handle 或 RenderDoc EID 字段。
- 这些 `callerPc` 是当前宿主/桥接运行时记录的调用地址，不能直接当成客体
  `libunity.so` RVA，也不能与静态 `0x18a98cc`、`0x23acf20` 做数值匹配。
- 同一会话的 1,724 条身份映射仍没有 `activeTransfer` 上下文。

## hypothesis

- 生产者探针捕获到了比之前静态窗口更高层或不同 ABI 包装路径，但快照只显示容器
  和中间记录，尚未到序列化身份边界。
- 三个调用点可能对应不同的运行时分发路径；当前不能从计数或地址判断其业务语义。

## blocked

- 尚未闭合 `sourceFile#PathID -> runtime object`。
- 尚未闭合 Material/Shader 到 RenderDoc EID 的同一对象关系。
- 下一步必须在生产者调用的同一上下文中观察真实对象加载/写回字段，或找到明确的
  序列化对象加载入口；不能把快照中的指针、计数或地址解释成身份。

## 2026-09-14 06:13 解析入口探针

### confirmed

- 静态入口候选为 `0x23a83b8`，首指令为 `0xff8301d1`；静态调用点包括
  `0x23a71c0`、`0x23a78ec`、`0x23a8984`、`0x23a8cf0`、`0x23a9244` 和
  `0x23a9de4`。
- 函数入口使用 `x0/w1/x2/x3/w4/w5`，调用后返回 `x0`；当前探针只读记录这些
  原始参数、返回值和有限快照。
- 4.5 实验没有安装该探针：运行时同一地址的首指令为 `0xaa0803e0`，与静态
  `0xff8301d1` 不一致，因此没有调用记录。

### observed

- 生产者探针本次仍可安装并保持客体不崩溃，但解析入口探针连续报告
  `instruction_mismatch`；这不是“函数未调用”的证据，而是目标代码与静态样本不一致。

### hypothesis

- 当前运行时映像可能经过了与离线 `libunity` 不同的代码变换、补丁或加载映射；
  仅凭静态 RVA 不能继续 Hook。

### blocked

- 解析入口的运行时地址/版本映像尚未闭合，不能启动 ABI Hook，也不能由此生成
  Material/Shader 身份关联。
- 下一步应先从实际加载的 4.5 `libunity` 映像建立可验证的代码签名或直接拉取运行时
  代码页；在首指令匹配前不再扩大 Hook 范围。

## 运行时映像核对

- MuMu 当前进程的映射路径为 4.5 客体的 `lib/arm64/libunity.so`，映射文件哈希与
  本地离线样本相同。
- 但映射地址 `0x4000233a83b8` 的实际字节为
  `e00308aa0b5e0094f303002ae00316aa`，离线文件同一 RVA 为
  `ff8301d1fe6f01a9fa6702a9f85f03a9`；多个静态调用点也存在同类差异。
- 因此当前证据指向 MuMu Native Bridge 对执行映像的运行时变换，而不是简单的
  文件版本或磁盘哈希不一致。该判断不等同于已经知道翻译缓存地址。
- 在放宽扫描条件、遍历可读的 Native Bridge `libunity` 映射页后，4.5 会话
  `HSR-4.5-MUMU_20260914_063057333` 仍得到 `signatureMatchCount=0`；解析入口
  调用数为 `0`，而生产者调用数仍为 `49`。因此“代码页未扫描到”这一实现缺陷
  已排除，当前阻塞位于 Native Bridge 的实际翻译映射。
- 详细字节证据：
  `artifacts/analysis/HSR-4.5/hsr45-runtime-mapped-code-20260914.json`。

## 匿名映射实验回退

- 会话 `HSR-4.5-MUMU_20260914_063518068` 曾临时开启匿名映射扫描。
- 客体在 TypeTree 生成前收到 `SIGSEGV`；logcat 栈指向
  `IsValidRuntimeTypeArray -> GetRuntimeTypeArray -> RunGuestDumper`，不是解析入口
  Hook 的调用证据。
- 原因是现有 `GetExecutableSections()` 同时服务通用 TypeTree 枚举器和实验扫描器，
  把 Native Bridge 的匿名翻译内存误作为 TypeTree 数组。该配置已撤销，后续不能再
  通过 `include_anonymous=1` 复用这条通路。
- 错误证据：
  `artifacts/HookSessions/native-material-hsr45/HSR-4.5-MUMU_20260914_063518068/runtime/typetree/android-errors-logcat.txt`。

## 独立匿名页签名回归

- 会话 `HSR-4.5-MUMU_20260914_064139297` 使用独立、限量的匿名映射读取器，
  没有把匿名页加入 TypeTree 区段。
- TypeTree 正常生成，客体未崩溃；扫描结果仍为唯一签名 `0`，解析调用 `0`，
  生产者调用 `50`。
- 因此可以排除“通用 TypeTree 区段隔离导致漏扫”这一实现问题；当前缺口确实是
  Native Bridge 翻译后的函数映射，而不是继续扩大静态签名范围。
 
## 2026-09-14 06:58 运行时首指令实验

### confirmed

- Profile 仍为 HSR-4.5-MUMU；未启动或读取 HSR 4.4 客体。
- 4.5 静态入口 0x23a83b8 的离线首指令仍是 0xff8301d1，运行时首指令为
  0xaa0803e0。该值来自同一 4.5 运行时映像的受限读取，不是模糊扫描结果。
- ARM64 guest probe 在运行时首指令精确匹配后报告
  installed_by_runtime_instruction；进程没有崩溃，TypeTree catalog 仍生成 7755 字节。
- 本次没有产生 unity.serialized-identity-resolve-call，因此没有把安装结果当作调用结果或身份语义证据。
- 证据会话：
  artifacts/HookSessions/native-material-hsr45-runtime-resolve-20260914/HSR-4.5-MUMU_20260914_065838667/。

### observed

- 同一事件文件中出现了后续 guest 模块加载尝试；它们的首指令为 0xf00ec8d1，均被拒绝，没有扩大 hook 范围。
- 会话结束时 MuMu 前台仍是 com.miHoYo.sdk.platform.SdkActivity/ComboSDKActivity，不是 Unity 游戏场景。

### hypothesis

- resolve-call=0 可能只是因为实验没有进入会触发该解析边界的游戏场景；这需要用户完成 4.5 登录并进入场景后用同一 Profile 重复验证。
- 也不能排除 0xaa0803e0 对应的是运行时包装入口而不是静态函数的完整语义，因此仍需真实调用事件确认。

### blocked

- 尚未获得一次带非空参数和返回值的 unity.serialized-identity-resolve-call。
- sourceFile#PathID -> runtime object -> Material/Shader -> RenderDoc EID 仍未闭合。

## 2026-09-14 readiness 修正

### confirmed

- 当前 HSR 4.5 包名仍为 `com.miHoYo.hkrpg`，前台 Activity 为
  `com.mihoyo.combosdk.ComboSDKActivity`。
- 当前设备不允许普通 ADB shell 读取 `/proc/<pid>/maps`，返回
  `Permission denied`；因此不能把 maps 读取失败解释为“没有加载
  libunity/libil2cpp”。
- 托管端已改为优先读取客体 `runtime-events`：看到
  `native-bridge-extra-load` 且触发库为 `libil2cpp.so`，或看到真实 Unity
  观察事件后，才认为已经进入 Unity 原生运行时。

### observed

- `profile-check profiles/HSR-4.5-MUMU.json` 通过。
- `dotnet build src/MyHookTool/MyHookTool.csproj -c Release --no-restore
  /p:UseSharedCompilation=false` 通过。
- 最新会话仍然是 resolve observer 已安装但 `resolve-call=0`，不能作为身份桥证据。

### hypothesis

- 之前的空结果主要风险之一是 readiness 信号太弱：工具可能在 SDK 登录层或无法读取
  maps 的状态下继续等待/结束。

### blocked

- 仍需要在实际 Unity 场景内获得非空 `unity.serialized-identity-resolve-call`，
  或定位另一个能直接输出 `sourceFile#PathID -> runtime object` 的边界。

## 2026-09-14 runtime TypeTree 复核

### confirmed

- 会话 `HSR-4.5-MUMU_20260914_065838667` 的客体输出
  `runtime/typetree/release.ttbin` 可被工具解码：HSR 4.5 runtime、2 棵树、152 个节点。
- 两棵树的字节内容相同，均为 `Material`（persistentTypeID 21），因此有效覆盖是
  1 个唯一类型、76 个字段节点；重复来自启动重试，不是新的字段覆盖。
- 两份 Material 树的字段完整性均为 `valid`：类型名和字段名非空，76/76 节点索引连续，
  75 个非根节点父级可解析，未发现结构错误。
- `ripper-logcat.txt` 记录了同版本 runtime 对 Material 调用 `+0xA8`，并明确报告
  `Validated native TypeTree Material nodes=76 strings=295`。

### observed

- 当前实际运行时 TypeTree 只覆盖 Material；Shader（persistentTypeID 48）尚未在本次
  native 实验中请求，因此不能声称 Shader 字段树已经获取。
- `release.ttbin` 是真实客体输出，不是 profile 中的离线 baseline；baseline 仍单独标为
  `runtimeGenerated=false`。

### hypothesis

- 只需在同一已验证 runtime ABI 下把实验的显式 class-id 从 21 扩展到 48，可能得到
  Shader 的字段树；这必须以新的非空节点和完整性校验为准，不能从 Material 推断。

### blocked

- 本次会话仍未进入实际游戏场景，因而没有 `unity.serialized-identity-resolve-call`；
  sourceFile#PathID、Material/Shader 与 RenderDoc EID 的直接同对象关联仍未闭合。
