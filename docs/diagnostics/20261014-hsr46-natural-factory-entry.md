# HSR 4.6 真实工厂入口调用里程碑

## 结论

已经取得当前 4.6 游戏自然执行的 ARM64 候选 Material 工厂入口事件，不再仅有安装成功、类型目录或对象快照。这个里程碑不代表字段级 TypeTree、Material/Shader 关联、AS 路径或 RenderDoc EID 身份桥已完成。

活动 Profile 保持 discovery-only。实验 Profile 和默认启动流程分离；未修改 AS、RenderDoc 或 ZZZ 的实现。

## confirmed

证据路径均相对于 Hook 仓库。

1. 首轮 `artifacts/HookSessions/hsr46-factory-entry-20261014/HSR-4.6-MUMU_20261014_073821411/`：Android PID 17109。`runtime/events.ndjson:83` 为独立汇编 stub 的真实自测试；84 行为安装；89 行起为自然入口事件；192 行为终态。93 条、93 个唯一 token、dropped=0，内部计时 50.685 秒。
2. caller 轮 `artifacts/HookSessions/hsr46-factory-caller-20261014/HSR-4.6-MUMU_20261014_074447959/`：PID 18626。83 行自测试、89 行 caller 窗口、193 行终态；93 条、dropped=0，内部计时 50.517 秒。
3. 错误传递修复后的回归 `artifacts/HookSessions/hsr46-entry-final-regression-20261014/HSR-4.6-MUMU_20261014_080635812/`：PID 21997。83 行自测试通过、89 行 caller 窗口、193 行终态；再次为 93 条、dropped=0，用时 48.725 秒。
4. 上述自测试使用本组件自己的汇编函数，由汇编独立记录预期 LR/SP，验证实际 callback 的参数、返回结果、原始 LR/SP 和恢复字节。不是人工调用游戏工厂；自测试事件不能充当游戏调用事件。
5. caller 窗口的运行时、设备磁盘、本机 4.6 ELF 的 16 字节完全一致：`00013fd6f30300aa800b00b476050034`。通过映射 file offset 和执行 PT_LOAD 得到 ELF VA `0x1ca3f60`，解码为 `blr x8`。文件偏移为 `0x1c9ff60`，不是同一数值。

## observed

- 原始参数为 x0=0x2c、x1=0x1。没有仅凭数值给它们赋予内存标签、创建枚举或身份语义。
- 写日志的 worker TID 与 callback TID 分开记录。eventSequence 是组件内序号；必须结合 session、PID、producer 和 architecture 使用，不能与 x86_64 Ripper 的序号混用。
- `closureLrRaw` 是 Dobby 内部 LR。`originalCallerLrRaw` 来自本仓库闭包保存的入站 LR 槽，并由独立 live self-test 验证；它依然不能单独排除 tail-call chain。
- 该 caller 的静态流从外层入口 x1+8 读取目标到 x8；将入口 x3 的低32位、w4、x5 分别传入 x0、w1、x2。调用后先保存返回 x0，再处理控制分支与登记/清理样路径。不能把目前 RTTI.hpp 的两参数声明当作所有类型的完整 ABI。
- 当前自然调用样本来自启动阶段。游戏标题画面是单独检查的，不宣称这些事件来自某个角色、技能或游戏内 Draw。

## 安全回归

- 宿主对 catalog-only Profile 拒绝旧 observer/native 操作。负例记录 `artifacts/status/20261014/catalog-guard-negative.log`；命中预期拒绝，Android PID 不变。
- 启动器隔离 MY_HOOK_、SR_、TYPETREE_RIPPER_ 环境，结束后还原。注入旧 SR_HSR45_MSHADER_CALLSITE_OBSERVER 的正例仍生成 catalog_only=1。
- 只使用本仓库隔离的 `third_party/dobby`；原 TypeTreeRipper 内的 Dobby 未修改。严格模式在提交前拒绝非四字节近跳板和重定位失败，不回退长补丁。
- executable buffer 写入失败向上传递；恢复失败不删除登记并返回成功。四字节入口采用对齐原子写入，权限恢复错误向上传递，缓存同步不因该错误而跳过。
- 控制写入故障：`artifacts/HookSessions/hsr46-owned-write-fault-20261014/HSR-4.6-MUMU_20261014_080323628/runtime/events.ndjson:90-91`。PID 20657，自测试 dobbyStatus=-2、passed=false、原始字节未改，随后拒绝。没有 factory-entry-installed，也没有游戏入口调用事件；用时 26.621 秒。
- 本轮只实测了正常路径与一次受控写入失败；没有覆盖所有操作系统/分配/权限故障，不声称所有失败路径都已充分验证。
- 宿主等待同 session/PID 的 terminal event 后才清理远端文件。缺终态或异常会停止目标 Android 游戏，不停止 Windows 或其他应用。

## 实现与使用

`src/GuestFactoryProbe` 是独立 ARM64 组件，不复用旧版地址观察器。`profiles/HSR-4.6-MUMU-factory-entry-probe.json` 保存当前构建的 Build ID、ELF VA 和原始代码窗口；启动时重新核验，不硬编码运行时地址。

构建需要 Android ARM64 NDK、CMake 和 Ninja。CMake 的 Dobby 默认指向仓库内隔离副本；可显式提供这些工具路径，不依赖 D 盘作为功能前提。测试本机缓存曾用外部 Dobby，当前构建缓存已改为仓库内副本。

实验运行 `tools/Run-HsrFactoryEntryExperiment.ps1`，传入 MuMuRoot、AdbSerial、Profile、Output；默认 ADB server port 5038 可替换。该脚本只接受显式实验 Profile。它重新启动游戏进程，不重新安装客户端、不重启整个 MuMu VM、不人工调用游戏工厂。

单次预算：最多128条原始调用、8个caller窗口、45秒采集；callback 只复制寄存器/时间/线程信息并发布预分配记录。worker 负责读取窗口与写文件。结束后关闭采集，补丁随该游戏进程退出释放；不在并发游戏线程运行时自动恢复成功入口。APK 文件不被改写。

`.hook` 和相邻 events 保存在本机 artifacts/HookSessions 下。当前 Header/source-only TypeTreeRipper 仍需本机工作树构建，未完成整个工具的独立公开克隆验收。上述实验配置不是正式发布版支持声明。

## blocked / 下一门槛

暂不人工调用工厂或 GetTypeTree。下一最小实验是在已定位的间接调用前后记录真实目标、原始参数与返回值，并用同线程有界 token 栈配对。必须先验证 BLR 重定位改变 LR 的行为及两个相邻四字节点位不会互相覆盖；不能把旧的 LR 一致规则机械套用。

配对成功也只证明实际分派和返回对象。对象登记/清理、字段树上下文布局、完整 TypeTree 节点以及 AS/sourceFile#PathID/Material/Shader/EID 仍需各自的直接证据。
