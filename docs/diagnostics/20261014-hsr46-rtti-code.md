# HSR 4.6 RTTI 与候选工厂代码证据

本轮仅采集原始结构与代码，不执行工厂、Transfer 或 TypeTree 函数。活动 Profile 仍为 discovery-only。

## confirmed

- 本机 ARM64 `libunity.so` SHA-256 为 `867e896398bef4225ddc7b07c062f686fed0af9d321eb980d1335ad01f688f9e`，磁盘 Build ID 为 `f048ec2098d0e7ed`。
- 三类型会话：`artifacts/HookSessions/hsr46-three-class-rtti-20261014/HSR-4.6-MUMU_20261014_064657501/`。`runtime/events.ndjson` 第 71、72、73 行对应 Material、Shader、Light，PID 11436、TID 11440、进程序号 4、5、6。
- 三个候选代码窗口各 512 字节；运行时、设备磁盘、本机 ELF 逐字节一致。离线审计见会话内 `runtime/rtti-code-audit.json`。

| 目录类型 | 源布局下 size | 候选文件偏移 | 候选 ELF VA | 512 字节 SHA-256 |
| --- | ---: | --- | --- | --- |
| Material / 21 | 264 | 0x221bc2c | 0x221fc2c | b5838398faee60c578e4a015bccb0a8f96ab5660c6b2b29669188b89c968b01a |
| Shader / 48 | 648 | 0x28ccdd4 | 0x28d0dd4 | a59bded9ce1bf273e3ba33371aad974f85a49c8d1809a6d3109723f8424c4b66 |
| Light / 108 | 88 | 0x1cbd7f4 | 0x1cc17f4 | 6541805b5ff176d2dc9fcb287a0d7fd764c80ce1859dab950cc7ea71d4c9784a |

这里的窗口长度不是函数长度；Material 的 512 字节窗口包含相邻函数。

- Material 重复会话：`artifacts/HookSessions/hsr46-rtti-repeat-20261014/HSR-4.6-MUMU_20261014_064543287/runtime/events.ndjson` 第 70 行，PID 10256、TID 10260、序号 4，代码 SHA 相同。工具内部计时 22.534 秒，含启动器外层计时 23.285 秒。
- 三类型会话内部计时 23.086 秒。没有 VM 重启、GPU 回放或新增游戏函数 Hook。
- `tools/test_audit_rtti_evidence.py` 的五项测试通过：字节匹配不能升级为 ABI、旧字节拒绝、错误字段偏移拒绝、非执行 PT_LOAD 拒绝、缺失字节拒绝。

## observed

- 源 RTTI 模板是 80 字节，factory 指针偏移 8，类型 ID 偏移 40，size 偏移 44。原始字节与目录数值一致；这只是局部布局观察，不代表整套游戏 ABI 已验证。
- 初始会话 `hsr46-rtti-code-20261014/HSR-4.6-MUMU_20261014_064021416/runtime/events.ndjson:70` 读取失败，不能作为工厂代码证据。
- 后续会话 `hsr46-rtti-read-method-20261014/HSR-4.6-MUMU_20261014_064334084/runtime/events.ndjson:71` 明确记录 `process_vm_readv` 返回 -1/errno 22；同进程 `/proc/self/mem` 的有界 `pread` 分别返回 80 和 512 字节。两个方法与错误均保留，不静默掩盖失败。
- 观察器为 x86_64，代码属于 AArch64 ELF。MuMu 中代码映射权限为 r--p；离线 ELF 的对应窗口位于执行 PT_LOAD。这不授权 x86_64 调用 ARM64 地址。
- 当前 Material 候选入口 VA `0x221fc2c` 保存入口 x0，取其低 32 位传递给分配路径；入口 x1 在使用前被覆盖。`0x221fc54` 指定大小 264，`0x221fc64` 调用 `0x1c8dba8`。
- `0x221fc8c-0x221fca8` 写入候选虚表 `0x2a9d6d0`；`0x221fd2c` 返回 x0。`0x221fd30-0x221fd38` 是全局分配状态为空时的初始化/重试路径。
- ELF 初始化代码 `0x16ae9e0-0x16ae9f4` 将该入口写入静态记录的 +8。数据映射和代码映射的偏移不一致，不能把代码段的 bias 套用到 RTTI 数据段。

## hypothesis

此入口行为符合接收内存标签、分配并初始化 Material 对象的工厂，但完整签名、创建模式语义及外层注册流程尚未证明。Runtime 四字节标签与观察兼容，RuntimeDev 十二字节标签不能视作同一 ABI。

## blocked

本轮没有字段级 TypeTree，没有真实工厂调用事件，没有 Material/Shader、sourceFile#PathID 或 RenderDoc EID 的身份桥。

下一门槛是同 ISA 的被动调用证据：先验证 ARM64 观察器载入，再只观察候选入口的游戏自然调用，记录寄存器、SP、LR 和同次调用上下文。不得依据地址邻近、名称、顺序、低位或 ResourceId 补链。RET 修补、对象人工创建、字段树生成必须另过参数、生命周期和上下文布局门槛。

## 重复方式

先使用新构建的 Ripper 运行 `tools/Run-HsrCatalogExperiment.ps1`，显式传入 MuMuRoot、AdbSerial、Profile、Output；默认只选 Profile 的 observeClassIds，可用 ClassIds 指定最多八个类型。该脚本只接受 read-only-runtime-catalog Profile。

再用 `tools/audit_rtti_evidence.py` 指定 events、当前版本 library、expected-sha256、profile、session-config、catalog 和 output。空记录或不一致返回失败；报告不声明 ABI、人工调用或资产身份成立。

反驳审计后的独立来源检查包括会话、PID、模块路径、Build ID、加载头、源布局配置、目录 size 和两种读取渠道的计数。15 项单元测试通过；三类型实机记录的严格报告保存在同会话的 `runtime/rtti-code-audit-strict.json`。先前的简化报告只作为当时的局部字节检查保留，不升级为完整来源验证。

本轮 native 源码仍位于本机 `References/TypeTreeRipper` 工作树，Hook 使用其构建产物。尚未完成可独立克隆构建的源码整合，不应把本轮本机验证描述为公开发行版已可用。
