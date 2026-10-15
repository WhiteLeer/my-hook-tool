# HSR 4.6 静态 Shader 小样本

## 结果与边界

从已核对的 4.6.0 APK 恢复了一组真实静态资源，不需要新的运行时 Hook。

- Shader：`miHoYo/CRP_UI/UI_Particle_OneChannel`，源 `629b601f14b97cc4d948f9d455e8a22c#1`。
- Material：`UI_ChallengePeakBoss_2002_Par`，源 `9477f7369cc2ba44f9e5331aca80e886#1`。
- Texture：`UI_Glow_94`，源 `4e5897dd8c30c6c4aafb4a77187197e4#1`。
- Material 的两个外部 PPtr 分别准确指向上述 Shader 和 Texture；AS 与独立 UnityPy 读取得到相同引用、贴图变换和五个 Float / 两个 Color 值。
- Unity 2022.3.62f1、D3D11、Gamma：验收工程实际离屏渲染得到星芒，Shader 错误 0、洋红像素 0、相对背景变化像素 39816。

这只通过了“静态程序恢复、引用解析、可编译且可见”的小样本门槛。没有同版本 RDC 对照，不宣称与游戏效果一致；没有恢复原粒子发射器、动画、UI 顶点颜色或运行时剪裁参数，也没有 runtime/EID 身份桥。

## 来源与数据流

APK：`artifacts/Updates/HSR-4.6/StarRail_4.6.0.apk`，SHA-256 `07234d7e0f7fe0d083bab06a9504fea0604d6c80187f518774e55d3270e7d123`。只读设备审计确认安装的 base.apk 与此一致。

1. `tools/audit_apk_shader_inventory.py` 在已处理的 APK Data 子集识别18个 Shader、32个 Material；Resources与一个 split容器未处理，不宣称完整APK覆盖率。未按通用引擎 TypeTree 盲读 Shader 字段；这类读取曾引发异常分配，已从扫描器移除。
2. AS 已有 `--shader_report` 读取11个自定义布局 Shader的元数据、24个引用这些 Shader的 Material。`tools/export_static_shader_metadata.py` 实际重新执行AS并绑定报告、输入容器与导出器的内容哈希；报告或输入被改动时后续步骤拒绝。源输入来自同一APK；旧 `--shader_blobs` 在平台记录处错位并触发报告 NullReferenceException，不能当作“没有代码”。本轮没有修改AS核心。
3. `tools/extract_apk_shader_program.py` 有界读取平台记录。当前对象比旧实现每个平台多一个 32 位字；原始偏移和值保存在清单，语义仍未知，不将其命名为某个猜测字段。此实验布局需显式启用，验证数组长度、所有字节范围、解压长度及对象尾部完全消费；禁止自动回退。
4. Vulkan program 0 经 LZ4 得到 3411 字节。`src/StaticShaderDecode` 使用已有 AS 的 SMOL-V 解码源码得到两个实际 SPIR-V 程序；阶段由 entry point reflection 判定。解码清单与 SPIR-V 内容哈希用于文件来源和完整性验证，不用于跨域对象身份推断。
5. SPIRV-Cross 输出实际 Vertex / Fragment HLSL。该 APK 的 Vulkan vertex 表项包含两个阶段和绑定表；fragment 元数据表项 6 无独立代码。样例只支持这个明确布局，不根据空代码生成替代 Fragment。
6. `tools/build_static_shader_sample.py` 用序列化 descriptor packet、反射的set/binding、完整buffer size及成员byte offset对齐名字；拒绝重复descriptor，并验证scalar type、dimension、rows与arraySize。12个使用项逐项记录在 `sample.json`。原程序算术保留；Unity适配仅涉及材质属性声明、矩阵列数据、顶点输入、采样器关联与Vulkan/D3D Y约定。

未识别的额外平台字仍使布局处于实验状态。程序可成功解压和反射，不等于当前完整 Shader TypeTree 已获取。未扫描 3112 个设备 BLOCK，更不代表 APK 已包含所有游戏 Shader。

## Unity 验证

样例：`D:/unity-extraction-validation/Assets/RenderDocShaderSamples/HSR-4.6/StaticParticleOneChannel/`。

- `UI_Particle_OneChannel.shader`：真实程序转换后的源码，未添加限制管线的 RenderPipeline tag。
- `UI_ChallengePeakBoss_2002_Par.mat`：原材质保存参数和原贴图引用。
- `UI_Glow_94.png`：原 ASTC 贴图解码。源 m_ColorSpace=0，样例关闭 sRGB、压缩和 mipmap，使用源 Clamp/Bilinear 设置。
- `_Preview/StaticShaderPreview.unity` 与 `.prefab`：固定白色顶点 Quad 和相机，Quad 是验收夹具，不是恢复的游戏模型。

预览明确使用中性 `_ClipRect`，关闭 `_UI_CLIP_RECT`；这些不是从材质中提取的运行时 UI 参数。

PNG同时与独立UnityPy的ASTC解码核对：最多相差一个8位量化步，4478个分量有差异；Y翻转的最大差异为211，已排除。量化差异和原对象/PNG哈希保留在 `sample.json`，不宣称两个解码器逐字节相同。12项CPU回归通过，覆盖越界、旧布局、报告来源、重复descriptor与类型不匹配等拒绝路径。

初次 Built-in 验证不可见且 changedPixels=0：恢复程序没变，只移除了 UniversalForward 专属 LightMode；Built-in 与 URP 默认 Unlit 路径均可选择该 Pass。之后隔离工程和验收工程均得到39816变化像素。原项目中的旧 Shader 和用户添加的 Pass 没有修改。

可重复入口：

```powershell
./tools/Test-UnityStaticShaderSample.ps1 `
  -UnityExe '<Unity.exe>' -Project '<validation-project>' `
  -Sample '<sample.json>' -AssetOutput 'Assets/RenderDocShaderSamples/HSR-4.6/StaticParticleOneChannel' `
  -Report '<outside-Assets>/unity-validation.json'
```

GPU 验证用 `Local\MyHookGpuValidation` 互斥锁串行执行。报告必须是本次新生成；超时只终止该验证进程。不截桌面、不修改系统配置，也不自动重启 MuMu。

证据根：`artifacts/StaticShaderSamples/HSR-4.6/`。最终状态是 `compiled_and_visible_not_game_reference_verified`，不是完整视觉验收。
