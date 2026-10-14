# HSR 4.6 只读类型目录实验

## 结果与边界

2026-10-14 实机安装包为 `com.miHoYo.hkrpg / 4.6.0 / versionCode 118`，已进入游戏场景。活动目标更新为 `HSR-4.6-MUMU`；4.4、4.5 的历史产物不改名、不冒充新版本数据。

目录实验用时 22.805 秒，取得 182 条 RTTI 类型记录与 69 条诊断事件。Material、Shader、Light、ParticleSystem 均在目录中。**目录不是字段级 TypeTree，也不是函数实际调用或对象身份绑定证据。**

实验关闭 Transfer、vtable 探测、native Transfer、native layout、对象身份与 Vulkan Draw observer；没有沿用旧版 RVA，没有调用类型工厂。普通活动 Profile 仍为 `discovery-only`，不开放未经验证的 native 调用。

## 可复查证据

以下路径相对于 Hook 仓库，生成物不提交为公开游戏资源。

- 版本：`artifacts/status/20261014/client-version.json`。
- 会话：`artifacts/HookSessions/hsr46-catalog-only-20261014/HSR-4.6-MUMU_20261014_044638353/`。
- `runtime/typetree/rtti-catalog.txt`：第 16 行 Material，第 28 行 Shader，第 66 行 Light，第 123 行 ParticleSystem。
- `runtime/events.ndjson`：第 1 行/eventSequence 0 为 runtime-build；第 2 行/eventSequence 1 为 loaded-ELF-PT_NOTE 身份记录；第 69 行/eventSequence 2 为 native-bridge-interface。这里的模块 Build ID 属于 Ripper 自身，不是游戏的 libunity；依据为 `android_impl.cpp` 中使用 `EmitRuntimeBuildEvent` 地址定位模块的实现。
- `runtime/guest-typetree-ripper.conf`：审查各 observer 开关与 `catalog_only=1`。
- `runtime/timing.json`：记录阶段耗时，类型目录就绪阶段为 19.025 秒。
- 类型目录 SHA-256：`f834b6c1c3e256f1ebfbf6508bc81ed37abfc7b4e2883bb14937916d2d7e534b`。
- 事件文件 SHA-256：`52474637a3b1a9ef9da5aba7e387bdaf19bc5d4e2afada76186dfe266f0d2e01`。

## 后续已执行的模块实验

ADB shell 无权限读取目标进程的 `/proc/<pid>/maps`，没有修改 root 设置或用地址推断绕过。改为在原有 catalog-only 采集器内部，读取自身 procfs 与 ELF 头；内存读取使用有返回值检查的 `process_vm_readv`，未调用游戏工厂或 TypeTree 函数。

- 首次模块会话：`artifacts/HookSessions/hsr46-module-catalog-20261014/HSR-4.6-MUMU_20261014_060404134/`，用时 24.371 秒。`events.ndjson` 第 70、71 行，PID 3899，eventSequence 3、4。
- 去重回归：`artifacts/HookSessions/hsr46-module-catalog-once-20261014/HSR-4.6-MUMU_20261014_061045297/`，用时 24.845 秒。第 69 行，PID 5291，eventSequence 3；只有一条目标模块记录。
- 两次均读取到目标 ELF machine 183（AArch64），加载 ELF 头与磁盘 ELF 头一致，磁盘 Build ID 为 `f048ec2098d0e7ed`，与当前 APK 库的离线检查一致。
- 这不代表加载的 note/text 完整匹配，也不代表函数 ABI 已验证。观察器架构与目标 ELF 架构分别记录。
- 同一 `events.ndjson` 含多个 Android 辅助进程的记录；eventSequence 是进程内序号，不是整个文件的唯一编号。分析必须结合 session、PID、TID、时间与行号。宿主 Windows PID 与 Android PID 不混用。

## 下一实验门槛

先区分 APK 的 ARM64 库、运行时 x86_64 目录环境与 native bridge。分别确认候选入口的构建标识、RVA、调用者与参数约定，再只对一个类型做一次限时字段树实验。类型名相同、大小相同或 Unity 版本字符串相同，都不能代替 ABI 验证。

字段树验收要求非空节点、完整读取与独立布局对照。身份链验收要求同一对象的直接调用证据；名称、地址相近、顺序、低位匹配、时间接近或 ResourceId 均不作为跨来源关联依据。
