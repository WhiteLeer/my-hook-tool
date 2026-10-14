# HSR 当前迭代目标

更新时间：2026-10-14。

目标：从真实游戏数据完成一个材质的 Shader 还原闭环，再证明序列化资产、运行时对象与 RenderDoc Draw 的直接关联。

## 验收顺序

1. 核对实际安装版本、运行库架构与构建标识，保存版本发现报告。活动 Profile 必须匹配这些证据；历史数据不得通过改名变成新版本证据。
2. 选一个真实 Draw，保留 Vertex/Fragment 原始程序、反射、贴图、常量及渲染状态，生成调用真实程序的 Unity ShaderLab、材质与独立验证场景。
3. 检查 Unity 编译、资源绑定和固定时间点渲染。编译成功不代表视觉一致；记录明确的差异和失败项。
4. 用最小实验证明同一 Material/Shader 对象的 sourceFile#PathID、运行时身份及 Draw 关联。名称、地址、顺序、时间接近与 ResourceId 不作为跨来源身份依据。
5. 至少三个不同程序样例通过回归后，再扩展批量导出与变体处理。

## 当前已核实基线

- 2026-10-14，MuMu VM 0 安装包 `com.miHoYo.hkrpg` 报告 `versionName=4.5.0`、`versionCode=116`。
- 官方更新入口提供 4.6.0。APK 经包名、版本和 SHA-256 核验后安装，实机现为 `4.6.0 / versionCode=118`。
- 活动目标为 `HSR-4.6-MUMU`，`profiles/active-hsr.json` 记录当前选择。4.6 libunity.so 与 4.5 的 SHA-256 不同；新 Profile 为 discovery-only，旧 native RVA / ABI 尚未迁移。
- 4.6 已更新游戏内资源并进入实际场景。只读 catalog 实验取得 182 条类型记录，用时 22.805 秒；详情见 `diagnostics/20261014-hsr46-catalog-only.md`。
- 新版本字段树与运行时身份实验仍等待 ABI 最小验证；类型目录不代表字段树或真实函数调用。
- 4.5 Material/Shader 非空 TypeTree 为 76/1551 节点，仅代表选定类型。
- 已有 4.5 捕获 `hsr45-rdc-transfer-20260916/.../capture_frame10930.srrdc` 在本机成功回放，含 354 个 Draw；用作离线回归样本。
- Material 到 Shader、sourceFile#PathID 到运行时对象、运行时对象到 EID 的直接身份链尚未闭合。
- 单 Draw 回归使用真实程序、常量与贴图，已校验 1352 个常量分量并修复采样/深度适配；仍有像素差异，未视觉验收。详见 `diagnostics/20261014-shader-regression.md`。

## 版本维护

版本启动器只负责选择 Profile。发现新版本时建立对应的独立 Profile 和运行目录；未经验证的 RVA、ABI、TypeTree 不从旧版本复制为已验证配置。AS、RenderDoc 与 Hook 各自记录实际支持和验证版本，避免仅靠分支名声称兼容。

本机产物写入 `artifacts/`，Unity 验证样例写入验收项目 `Assets/RenderDocShaderSamples/` 的对应版本目录。路径由命令参数传入，文档中的本机实验路径仅用于证据定位。
