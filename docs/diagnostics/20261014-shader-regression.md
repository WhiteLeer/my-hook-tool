# 单 Draw Shader 回归记录

本例使用历史 HSR 4.5 捕获 `capture_frame10930.srrdc` 的 EID 1720，不是 4.6 实机 Shader 兼容性证明。

## 已验证与已修复

- Vertex/Fragment 使用捕获中的真实程序，经 SPIRV-Cross 转为 HLSL；不是手写近似特效。
- 1352 个常量分量经过原始字节与解码结果逐项校验。修复了 RenderDoc MCP 常量读取遗漏 Vulkan dynamic offset 的问题；旧错误证据会被生成工具明确拒绝。
- 源纹理绑定视图为 `R8G8B8A8_SRGB`。Gamma Unity 项目覆盖导入纹理的 sRGB 视图，因此将原始 texel 解码为线性浮点数据后再过滤；没有在采样之后简单做幂运算。
- 独立 GPU 控制实验确认了本机 ShaderLab 的反向 Z 比较适配；背景回读误差为零，深度上传不会修改颜色附件。
- 同一 Shader 外部头文件更新未可靠反映到图形结果。现在把实际源码内嵌到 ShaderLab；独立 HLSL 文件仅供诊断，不作为最终 Shader 的依赖。
- 贴图 Y 翻转的单变量对照更差，已否决。当前保留捕获原始 texel 方向。

## 仍未验收

重复回归结果：源 Draw 改变 989 个像素，Unity 改变 1163 个像素，重合 980 个像素，联合绘制区域平均 RGB 最大分量误差约 0.00899，最大误差 0.125。331 个像素超过附件的一档量化精度。**98.99% 是变化像素覆盖率，不是效果相似度；本例仍为 comparison_pending。**

待排查的候选包括源程序的 RelaxedPrecision、实际插值/过滤精度和跨 API 深度/混合差异；这些是候选，不是已证实原因。不能因为源码编译成功就宣称完全还原。

RenderDoc PostVS 返回全零，PixelHistory 未报告目标 Draw，DebugPixel 后发生设备丢失。这些诊断结果无效。默认探针已移除相应 GPU instrumentation；保留普通输入/状态/附件读取和显式启用的 Vertex 指令调试。GPU 回放与 Unity 回归使用同一互斥锁串行执行；不自动重启系统或其他应用。

## 路径与重复执行

生成物与日志位于 `artifacts/ShaderSamples/HSR-4.5/event1720/`，不进入公开游戏资源 Git。Unity 样例位于验收项目的 `Assets/RenderDocShaderSamples/HSR-4.5/CapturedGeometry/`，包含 Shader、纹理、网格、材质、Prefab 和预览场景。

预览目录中早期导入用的三个 `Reference_*.exr` 已不被当前验证器使用。环境阻止了本轮删除，暂时保留，不把它们算作当前 Shader 依赖。

该样例冻结了一次 Draw 的矩阵、常量与输入，不是已恢复原始 Material 身份的通用游戏材质。精确原帧比较仍需捕获的背景和深度，由独立批处理验证器恢复。预览场景本身不具备完整原游戏环境。

入口：`tools/export_rdc_evidence.py` 采证，`tools/reconstruct_rdc_sample.py` 校验并生成，`tools/Test-UnityCapturedDraw.ps1` 运行独立 Unity 回归。路径由参数传入，不绑定固定盘符。证据原始字节、映射不唯一、支持范围之外或 GPU 失败时必须明确报错，不以近似 Shader 兜底。
