# HSR 4.6 粒子字段与小规模预览

## 结论与边界

当前活动版本为4.6.0/118。本轮只处理APK内的UI粒子样本，不修改已经通过验证的角色、材质或Shader，不启动MuMu，不修改RenderDoc。

取得可见的金色闪光/碎片粒子预览，不是完整头像框、玩法特效、角色渲染或Timeline还原。未作同版本游戏/RDC像素验收。

## 字段证据

历史原始树：`artifacts/HookSessions/native-particlesystem-current-20260911-r3/HSR-4.5-MUMU_20260911_081857242/runtime/typetree/ParticleSystem-full.json`，4439节点、persistentTypeId=198、transferFlags=256。来源仍标4.5。

4.4合并测试树只有4438节点。除索引偏移外，差异是节点3355的 `Base.TrailModule.curveTension`。这解释了应用于当前对象时的后续错位；没有4.4完整原始树对照，不能断言是历史合并程序丢字段还是游戏版本新增字段。

新的候选文件 `artifacts/StaticShaderSamples/HSR-4.6/schema-candidates/native45-particle-exact.structs.dump` 对198节点逐字段无损转换；199来自历史外部树，独立验证，不声称来自同一个native dump。来源/文件SHA及差异见同目录provenance报告。

`schema-traces/current11/`：11个当前对象全部在原始长度结束；没有偏移补偿、默认填充或复制opaque尾。首次拒绝点只表示读取器边界/策略拒绝，不表示最早真实错位。

`schema-traces/gold-independent/comparison.json`：3个PS、3个Renderer，7450个标量位级一致。AS的ObjectByteSize/ParsedBytes/RemainingBytes/Error一起核验；读取真实GameObject反向组件列表；非空PPtr按外部文件表定位CAB、PathID、类与目标对象hash。禁止重复组件身份及flatten路径。

AS额外生成的方便字段单列为asUncomparedFields，不当成源字段。所有98个PPtr子标量参与对照；9处非空引用可解析，其余89处为空。不存在本次PS/Renderer必须使用、但缺失的非空资源引用。

## Unity 导入与显示

流程：

1. AS以显式候选树导出小包，198/199走外部树；不用旧Importer导入整个UI包。
2. `compare_prefab_schema_trace.py` 生成独立对照与指针证据。
3. `prepare_static_particle_sample.py` 按sourceFile#PathID选择既有已还原材质，生成外部plan。包含参数源入口、文件hash及未恢复组件上下文。
4. `StaticParticleSampleBuilder` 再读取原package和comparison，逐项核对plan里的层级、TRS、完整组件集合、参数及材质PPtr，拒绝plan内擅改数据。
5. 直接写Unity支持的原生字段；从源值独立生成期望，不以setter后得到的值作验收基准。曲线保留时间、切线、权重、weightedMode、wrap；Gradient使用明确的16位时间适配。
6. 核对写后与Prefab重载后的1531项断言，包括标量、实际使用曲线/渐变、数组长度、null引用及材质槽。若source开启自定义vertex streams则另作API读回断言；当前3Renderer均未开启。
7. 原生PS Prefab先保存，随后仅在预览场景使用显示adapter。固定种子只属于预览实例，不改源Prefab。

原PS容量0/2/3保留；Root的Emission=false和3个Renderer.enabled=false均保留。原包具有Coffee.UIParticle组件，仅有其header，没有关闭Renderer的实际调用证据。

显示adapter通过Unity的 [BakeMesh](https://docs.unity.cn/jp/2022.3/ScriptReference/ParticleSystemRenderer.BakeMesh.html) 将粒子几何交给普通网格Renderer；这不是对Coffee.UIParticle、Canvas或SmoothMask脚本的完整重建。

常量模式里未使用的空曲线会被当前Unity自动展开零关键帧。工具不把此行为声称为源曲线恢复：空且未使用的槽位明确列入unsupported；真正使用的空曲线拒绝，需要专门验证的适配。

## 可看的结果

最新目录：`D:/unity-extraction-validation/Assets/RenderDocShaderSamples/HSR-4.6/StaticGoldParticleEffect/`。

- 原生数据Prefab：`HeadFrame226003_Particles.prefab`。单独放入普通场景不承诺可见，因为源Renderer关闭。
- 预览场景：`_Preview/StaticParticlePreview.unity`。打开后显示固定2秒样本，Play时5秒显示循环；时间/种子为显式预览条件。
- 两份材质复用StaticParticleOneChannel与StaticParticleCard的真实引用资产，未新写近似Shader。
- 原始package、plan、参数JSON、日志、图片与审计报告在Hook artifacts中，不放入最终Prefab目录。
- 先前StaticGoldParticles和StaticGoldParticlesVerified属于开发阶段中间产物；本轮清理命令被环境策略拒绝，保留，不当作最新验收输出。

最新验证：`artifacts/StaticShaderSamples/HSR-4.6/particle-effect-validation/validation.json`。

0.5/1/2/4秒均可见、洋红像素为0；2秒重复图像与场景重开图像SHA一致。Shader/材质/依赖资产文件构建前后不变；Unity当前活动场景恢复，未置顶或截桌面。针对参数、缩放、遗漏组件、材质PPtr和材质源字段的5个篡改反例被拒绝。31项既有CPU回归、7项trace回归、3项比较回归通过。

验证场景使用独立scene-culling位，避开已加载场景与相机的覆盖；原有相机不改动。一个默认scene-mask的探针相机在同视点、同粒子layer下无法绘制预览几何，changedPixels=0；正常预览相机可绘制。保存给用户的场景使用正常默认mask，打开后不需要特殊配置。

## 未闭合项

- 每个PS仍有194项unsupported，每个Renderer有23项。多数PS项为未使用空曲线、部分曲线rotationOrder和自定义扩展；不能等同于194项正在影响效果的缺失，也不能宣称字段完整映射。
- Coffee.UIParticle、SmoothMask、Image/Canvas布局及原生自定义Renderer扩展没有完整行为还原。
- 候选树兼容范围限于本批原始对象和字段hash，不是全游戏4.6 TypeTree保证。
- 下一阶段优先选一个当前版本、依赖明确的玩法特效，按同样的字段/引用/保存/视觉门槛推进，再扩到角色或Timeline。
- Material/Shader运行时对象到RenderDoc EID的直接身份链本轮没有新增证据。
