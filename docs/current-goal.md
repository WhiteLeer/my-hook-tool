# HSR 当前迭代目标

更新时间：2026-10-14。

目标：优先从当前 HSR 静态资源恢复 Shader 程序、材质参数和贴图，生成 Unity 可用 ShaderLab，以实际渲染和 RDC 验证。TypeTree 与运行时身份桥只针对明确的数据缺口推进，不作为所有 Shader 恢复的前置条件；禁止猜测映射。

## 验收顺序

1. 核对实际安装版本、运行库架构与构建标识，保存版本发现报告。活动 Profile 必须匹配这些证据；历史数据不得通过改名变成新版本证据。
2. 先选一个静态 Material，以真实 PPtr 定位 Shader 和 Texture；保留容器、程序与字段的原始证据。独立解析失败时报告具体断点，不用手写近似 Shader 填补。
3. 从实际编译程序生成 ShaderLab，检查 Unity 编译、资源绑定和固定渲染。编译或可见不代表游戏视觉一致；同版本 RDC 对照另设验收门槛。
4. 只有静态信息缺失或精确 Draw 对应确有必要时，才验证 TypeTree/ABI 并补充 sourceFile#PathID、运行时对象和 EID 的直接关联。名称、地址、顺序、低位匹配、时间接近与 ResourceId 不作为跨来源身份依据。
5. 至少三个不同程序样例通过回归后，再扩展批量导出与变体处理。

## 当前已核实基线

- 2026-10-14，MuMu VM 0 安装包 `com.miHoYo.hkrpg` 报告 `versionName=4.5.0`、`versionCode=116`。
- 官方更新入口提供 4.6.0。APK 经包名、版本和 SHA-256 核验后安装，实机现为 `4.6.0 / versionCode=118`。
- 活动目标为 `HSR-4.6-MUMU`，`profiles/active-hsr.json` 记录当前选择。4.6 libunity.so 与 4.5 的 SHA-256 不同；新 Profile 为 discovery-only，旧 native RVA / ABI 尚未迁移。
- 4.6 已更新游戏内资源并进入实际场景。只读 catalog 实验取得 182 条类型记录，用时 22.805 秒；详情见 `diagnostics/20261014-hsr46-catalog-only.md`。
- 新版本字段树与运行时身份实验仍等待 ABI 最小验证；类型目录不代表字段树或真实函数调用。
- 4.6 Material、Shader、Light 的 RTTI/候选代码窗口已采集并与设备磁盘、本机 ELF 逐字节核对；Material 在独立进程中重复通过。工厂完整签名、对象生命周期和字段树上下文尚未确认，详见 `diagnostics/20261014-hsr46-rtti-code.md`。
- 4.6 独立 ARM64 入口观察器已取得三次启动阶段的真实自然调用，每次93条、无记录丢失，并确认当前版本的 caller 代码窗口。受控写入失败测试正确拒绝，未安装游戏入口。没有人工调用游戏工厂；仍未取得字段树或跨源身份链，详见 `diagnostics/20261014-hsr46-natural-factory-entry.md`。
- 4.5 Material/Shader 非空 TypeTree 为 76/1551 节点，仅代表选定类型。
- 已有 4.5 捕获 `hsr45-rdc-transfer-20260916/.../capture_frame10930.srrdc` 在本机成功回放，含 354 个 Draw；用作离线回归样本。
- Material 到 Shader、sourceFile#PathID 到运行时对象、运行时对象到 EID 的直接身份链尚未闭合。
- 单 Draw 回归使用真实程序、常量与贴图，已校验 1352 个常量分量并修复采样/深度适配；仍有像素差异，未视觉验收。详见 `diagnostics/20261014-shader-regression.md`。
- 4.6 APK 静态小样本已恢复 `miHoYo/CRP_UI/UI_Particle_OneChannel`、一个真实引用材质与 `UI_Glow_94`。验收工程离屏渲染可见、Shader 错误为零；平台记录仍有一个未识别字，不宣称字段树完整或游戏视觉一致。详见 `diagnostics/20261014-hsr46-static-shader-sample.md`。
- 静态恢复现有2个原Shader名称、3组实际Vertex/Fragment程序组合、4个材质样例；新增UVMove的 `_CL2_X` / `_CL2_RG` 真实关键字分支、多贴图与固定0/1/2秒回归。18项CPU测试和隔离构建安全检查通过。达到小样本程序门槛，但仅覆盖UI夹具，尚未获得玩法Shader及同版本RDC像素一致性；详见 `diagnostics/20261014-hsr46-static-uvmove.md`。
- 原来的两个4.6 Prefab预检失败仍保留为反例。随后发现4.4合并树相对4.5原始粒子树少了 `TrailModule.curveTension`；采用带来源记录的4.5候选树，当前11个粒子对象严格消费成功。不是新取得的4.6运行时树，也不推广为所有4.6资源兼容。
- HeadFrame226003的3粒子/3Renderer通过独立字节对照：7450个标量值无差异，非空PPtr目标及GameObject反向组件引用已核对。新增粒子专用预览，不走旧Importer的默认补全或猜测材质路径；1531项源值派生断言在写后和Prefab重载后通过，固定时间重复与场景重开图像一致，原Shader/材质保持不变。仅是粒子显示实验，仍有自定义字段和UI包装缺口；详见 `diagnostics/20261014-hsr46-particle-schema-preview.md`。

## 版本维护

版本启动器只负责选择 Profile。发现新版本时建立对应的独立 Profile 和运行目录；未经验证的 RVA、ABI、TypeTree 不从旧版本复制为已验证配置。AS、RenderDoc 与 Hook 各自记录实际支持和验证版本，避免仅靠分支名声称兼容。

本机产物写入 `artifacts/`，Unity 验证样例写入验收项目 `Assets/RenderDocShaderSamples/` 的对应版本目录。路径由命令参数传入，文档中的本机实验路径仅用于证据定位。
