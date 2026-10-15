# HSR 4.5 Post-Transfer 最小实验

## 目的

只验证调用时序，不同时改变 vtable 槽位、Transfer 布局、ClassID 或解码器。
目标是判断：将 native TypeTree 调用从 `HookedTransferAtOffset` 的当前线程路径
移到原始 Transfer 完全返回后的安全边界，是否能在不崩溃的情况下产生非空字段树。

## 当前基线

- 当前包装函数在 `android_impl.cpp:4645-4721` 中调用原始 Transfer 后，仍在同一
  个 Hook 包装栈内执行 `RunGuestDumper()`。
- 目标函数 `libunity.so` RVA `0x22086cc` 会通过 `transfer + 0x30` 调用运行时
  上下文，并读取 `+0x58`、`+0x5c`。
- 当前 Native-on-Transfer 实验曾在 `0xdead1007` 崩溃，不能作为成功或 ABI 证明。

## 单变量设计

保持以下条件不变：

- Unity 客户端：HSR 4.5.0 / Unity 2019.4.34f1；
- 目标类型：只选 ClassID 21 Material；
- 已验证的 vtable 槽位与 Transfer 参数不变；
- 输出格式、事件字段、TypeTree 校验器不变；
- 不启用身份猜测、RenderDoc 映射或地址/顺序关联。

唯一变化：原始 Transfer 返回后只写入一个一次性、带对象和 Transfer 快照的待处理
记录；由已确认的 Unity 主线程安全回调消费该记录并执行一次 native 调用。若当前
代码没有这样的安全回调，则实验状态为 blocked，不得退回到当前 Hook 栈内重入。

## 必须记录的字段

每次实验记录：

- 原始 Transfer 的进入/返回事件编号、线程标识和调用者 RVA；
- object、object vtable、transfer 的不透明地址，仅用于同一事件的关联，不赋予
  PathID 或 InstanceID 语义；
- `transfer + 0x30`、`+0x50`、`+0x58`、`+0x5c` 的只读快照；
- native 调用开始、返回或进程退出事件；
- `release.report.json` 的 treeCount、nodeCount、fieldIntegrity；
- 客体退出码和 logcat 中的崩溃信息。

## 成功标准

同时满足以下条件才算成功：

1. MuMu 客体不退出，且原始 Transfer 行为未改变；
2. 生成非空 Material TypeTree，字段完整性校验通过；
3. 至少一个字段节点及其字符串表能由同一份 TypeTree 输出直接验证；
4. 结果可重复两次；
5. 不使用名称、地址、调用顺序、数量、ResourceId 或哈希推断任何资产身份。

若只满足“不崩溃”或只产生 catalog，实验仍为未通过。

## 当前状态

设计完成，尚未启动。原因是当前工作区没有已证明的“原始 Transfer 完全退出后
的安全 Unity 主线程消费点”；在该边界被静态或运行时证据确认前，继续 native
调用只会重复已知崩溃。

## 主线程边界审计结果

该阻塞已具体化：Windows 实现存在 `g_dumperReadyOnMainThread`、主线程探针和
`DetourGenerateTypeTreeNative` 的主线程路径（`windows_impl.cpp:431-454`），而
Android 实现的 `ScheduleGuestDump` 只创建 `std::thread` 延时执行
`RunGuestDumper`（`android_impl.cpp:4836-4849`）。Android 的
`HookedTransferAtOffset`（`android_impl.cpp:4645-4721`）也没有可见的 Unity
PlayerLoop、消息泵或主线程 dispatch；它只在当前 Hook 栈中调用 native dumper。

因此“把当前 detached worker 延时缩短”不能证明时序正确；下一次代码实验必须
先获得 Android Unity 主线程的真实回调/调用点，或明确采用原始 Unity 回调内部
的 after-return 安全点。否则只能继续做只读观测，不能声称已解决 ABI 问题。

## 同类实验的单变量对照

历史 Material 实验与 2026-09-14 崩溃实验的关键差异首先包括一个已确认的 ABI
配置；对所有历史产物复核后，`0xa8` 的结果不是单次偶然：

- 成功实验 `native-material-repeat-20260911`、`native-material-current-20260911`、
  `native-material-single-20260911-r2` 和 `native-material-identity-callerframe-20260911-r3`
  均使用 `vtable_offset=0xa8`，其中可读取的报告均显示 `76` 节点且
  `fieldIntegrity=valid`；
- 崩溃实验 `native-material-transfer-20260914-r1`：`vtable_offset=0xa0`，客体在
  native 调用后以 `SIGSEGV` 退出，TypeTree 只有 31 字节头部。

这证明 `0xa0` 不能作为当前这条 Material native TypeTree 路径的默认槽位；同时
也不能把 `0xa8` 推广到所有类型或所有客户端。下一步应优先统一报告发现逻辑，
再在同一客户端、同一 ClassID=21、同一输出配置下复现 `0xa8`，并验证身份观测
是否仍能与非空 TypeTree 同时存在；在此之前不调整 Transfer 结构或身份映射逻辑。

另外，`Program.cs:3007-3009` 要求 native 实验显式传入 `--vtable-offset`，而
`Program.cs:3291` 的缺省值是 `0x90`；profile 当前只提供 `observeVtableOffset=0xA0`，
不会自动把它转换为 native 槽位。因此“配置为 HSR-4.5”本身不足以复现 `0xa8`，
实验记录必须同时保存最终生成的 guest 配置。
