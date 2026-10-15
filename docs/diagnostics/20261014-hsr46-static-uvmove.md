# HSR 4.6 静态 Shader 多贴图与 UV 分支回归

## 本轮结果

没有覆盖或修改首个星芒样例。其 Shader SHA-256 仍为 `6760850a63e12d511f27c27c8d361eeedb51735168a91f92b994e9f99299a883`。

新增三组可在 Unity 打开的静态材质样例：

| 目录 | 原材质 | 实际程序 / 关键字 | 已核对的贴图输入 |
|---|---|---|---|
| `StaticParticleCard` | UI_ChallengePeakFrame_1006_Par | UI_Particle_OneChannel / 空关键字 | UI_Obj_Lizi_1001 |
| `StaticUvMovePattern` | UI_ChallengePeakFrame_1004_Pattern | UI_Particle_UVMove / _CL2_X / program2 | TPDiamondPattern、Eff_Mask_RotateTest_C |
| `StaticUvMoveNoise` | UI_ChallengePeakFrame_1001_Wenli | UI_Particle_UVMove / _CL2_RG / program0 | UI_Noise_10、Eff_Noise_699_02，以及原 Shader 声明的白色 Mask 默认值 |

这些是2个原Shader名称、3组不同的实际Vertex/Fragment程序组合，包括之前的OneChannel；不是3个不同原Shader，也不是完整粒子Emitter还原。

样例根目录：`D:/unity-extraction-validation/Assets/RenderDocShaderSamples/HSR-4.6/`。每组均包含材质、贴图、PreviewQuad、`_Preview/StaticShaderPreview.prefab` 和 `.unity`。Card直接引用原星芒目录的同一Shader资产，不重复创建同名Shader。

## 严格保留的数据

- 同版本APK的Shader与材质PPtr，不从名称推断跨域身份。
- AS元数据执行快照、输入/导出器/报告哈希；本轮输入另存 `UI_Particle_UVMove/inputs`，没有改写之前元数据证据。
- 实际关键字 `_CL2_X` / `_CL2_RG` 和各自的不同SPIR-V内容。硬件tier重复引用同一个程序，不据此生成重复文件。
- 多个Texture槽位、原Scale/Offset、WrapU/WrapV、FilterMode与颜色空间标记，独立Material与ASTC读取交叉检查。
- `_MaskTex` 不在Noise材质的保存槽位中；原Shader明确声明默认white，故样例保留默认，而不是为缺失资源猜配一张贴图。实际非空PPtr缺失仍报错。

只扩展Unity适配层：Texture ST、UV0 vec4输入、源 `_Time` 到Unity时间的映射、多个纹理槽位和源码相同的Shader复用。没有重写实际SPIR-V计算逻辑。

## 预览与MCP

当前Unity为2022.3.62f2。通过现有MCP服务调用项目内 `build_static_shader_sample`，生成并离屏渲染样例，不重启或置顶Unity，不截桌面。

- 使用Additive测试场景；结束后关闭它，恢复原active scene。
- 捕获选择一个当前未被Renderer占用的layer；Scene/Prefab保存相同layer，相机保存对应cullingMask，隔离工程重新加载后核对。预览场景建议单独打开；不能保证未来其他场景不占用该layer。
- 隐藏参数 `_PreviewTime=-1` 使用Unity引擎时间。验证时固定为0、1、2秒；验证结束恢复-1，Play模式下无需附加运行时脚本即可流动。
- MCP后续只读检查确认active scene仍是原星芒场景，编辑器未聚焦且sceneDirty=false。
- 新MCP桥在 `unity/Mcp/`，仅有Unity-MCP插件的工程才需要复制它；无插件的隔离工程只使用普通Builder。

固定时间点回归：所有输出Shader错误0、洋红像素0。最新Pattern相对0秒的1/2秒变化像素为35467/52762，Noise为191482/195126。不能把一张“可见”的截图当作UV动画通过。

证据：`artifacts/StaticShaderSamples/HSR-4.6/UI_Particle_UVMove/mcp-{pattern,noise,card}/validation.json`。报告状态仍为 `compiled_and_visible_not_game_reference_verified`。

## Gamma项目中的sRGB采样

Noise两张原贴图m_ColorSpace=1。MCP实际检查显示：即使Importer.sRGBTexture=true，当前Gamma工程仍以 `R8G8B8_UNorm` 加载，未进行预期的sRGB采样解码。

单变量修复：在该情况下把每个RGB texel按IEC sRGB曲线转换到线性，保留alpha，上传到RGBAFloat Texture资产，再以原Wrap/Filter采样；不能采样后再做pow，因为那会改变过滤语义。没有改变Shader程序、原材质参数或全项目颜色空间。

原PNG仍保留，材质在Gamma环境使用 `_LinearSampling.asset`。MCP确认实际GPU格式为 `R32G32B32A32_SFloat`。修正前截图和报告位于Noise的 `before-srgb`；修正后的可见性/时间回归重新通过。此项建立的是声明颜色空间的采样契约，还不是与原游戏Draw的像素相等证明。

## 构建安全回归

只保存本次输出的材质、网格和转换贴图，使用 [SaveAssetIfDirty](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.SaveAssetIfDirty.html)，不主动调用全局SaveAssets。实际回归发现：Unity的其他导入/场景保存API仍可能隐式保存dirty资源，因此构建前检查已加载、路径在Assets内且在输出目录外的persistent dirty资源；发现即拒绝，不代替用户保存或还原。此检查不是对Editor全部未加载资源、Importer与插件行为的完整隔离承诺。

输出目录属于本次允许重建的范围。重建时先恢复Shader默认值，再应用保存参数；v2 manifest在复制前和导入后核对Shader字节哈希及实际名称。哈希仅用于文件未变验证，不作为游戏对象/EID身份。

`unity/Tests/StaticShaderSampleRegression.cs` 仅在隔离batch工程运行。最新 `safety-regression/validation.safety.json` 通过：

- 输出目录外dirty sentinel被拒绝；磁盘字节、内存值、dirty标记和active scene保持不变。
- 重建不会沿用旧材质的未保存参数。
- 故意让PNG目的路径成为目录触发失败，RenderTexture、预览时间和active scene正确清理/恢复。
- 不接受变更的Shader指纹。
- 从磁盘重新加载保存场景，核对renderer layer和相机mask。
- 对Gamma转换纹理逐texel核对IEC曲线、alpha、RGBAFloat、mip数量和Wrap/Filter。

捕获后的 `_PreviewTime=-1` 仅定向保存本次材质，避免自身遗留dirty状态阻断下一组。MCP调用失败必须产生非零退出码，不以“连接成功”替代操作成功。

## 当前BLOCK小样本

只读审计观察到当前M_BlockV引用 `BlockV_cdf91f78dcbfe53d593c8c58bd76816e.bytes`，表中row3118/offset0x1246c对应 `3923734bdd71c9835f4fde159b7af047.block`。本机仅拉取该270777字节文件，MD5与名称/审计值一致。AS解析源为 `CAB-9825e2dae8d81f4ff7cf06b3facaea2e`，Shader报告为0个已解析Shader。

AssetMap为minimal模式，只列出44 Animator与1853 MonoBehaviour；不能把它当作完整ClassID库存。Shader报告的Material列表只保留引用所选Shader的材质，所以这里materials=[]也不能证明没有Material。

本次未从该BLOCK得到玩法Shader，也未扩大到整个缓存。主执行随后尝试把完整BlockV副本补到证据目录时ADB已offline，该副本未保存；当前索引行属于只读审计观察，不提升为本机独立复验。设备保持原状，没有重启MuMu或安装Hook。

## 复现入口

Python 3.11环境使用 `tools/requirements-static-shader.txt`；它与旧RDC脚本的requirements分开，避免改写其他环境的依赖。AS CLI、SPIRV-Cross、容器、元数据和输出位置均通过参数传入，不依赖本机D盘布局。

生成入口为 `tools/build_static_shader_sample.py`，需要同次元数据/程序解码/贴图证据。离线构建通过 `tools/Test-UnityStaticShaderSample.ps1` 的UnityExe、Project、Sample、AssetOutput、Report参数；加 `-SafetyChecks` 在隔离工程执行上述反例。不要把已打开的用户项目作为batch测试工程。

已有Unity-MCP的项目可复制普通Builder和 `unity/Mcp/StaticShaderSampleMcpTool.cs`，使用 `build_static_shader_sample`；没有插件的项目仅使用普通Builder。验收场景的Play模式直接使用Unity时间，不依赖附加预览脚本。

## 残留边界

- 平台记录的额外32位字语义仍未知。
- 18项CPU回归通过；只有小样本覆盖，不能声称通用导出已完成。
- 未取得同版本游戏Draw的参考图，无法认定灯光、最终合成或整体游戏视觉一致。
- UI Quad是夹具，没有恢复原发射器、UI网格、顶点颜色、动画或脚本驱动。
- 当前APK索引明确引用的238KB startup BLOCK已被AS解开成7个CAB，但该次Shader报告为0 Shader；同样不能当作玩法Shader样例。
