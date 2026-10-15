# HSR 4.6 角色技能命中特效验证

## 本轮结果

目标为源 Prefab `Eff_Avatar_Gilgamesh_00_Skill02_hit`，不是 UI 粒子。当前客户端再次核对为 4.6.0 / versionCode 118。

Unity 场景位于：

`D:/unity-extraction-validation/Assets/RenderDocShaderSamples/HSR-4.6/GilgameshSkill02Hit/Effect/_Preview/StaticParticlePreview.unity`

打开场景后点击 Play，可循环预览命中闪光。循环时钟只属于预览场景；原始数据 Prefab 不附加预览脚本，不延长粒子寿命，不改变源容量或固定种子。此命中特效不是完整技能演出；尚未证明游戏在何时、对哪个目标触发它。

## 源引用与参数

| 对象 | 源身份 |
| --- | --- |
| 根 GameObject | `CAB-b81cc79b7f4519311f1bae1bafad12bf#-6617258281910355763` |
| 材质 | `CAB-e9d89b566595430af54146d3c2618685#-7211189983586654392` |
| Shader | `CAB-d140c6d290d70e92fb3a5e385720ea83#-7177827563780441733` |
| 纹理 | `CAB-01f25540e4db4735f715b3553f22b1e3#-8639642188977018165` |

名称只用于候选发现。Renderer → Material → Shader/Texture 均按序列化外部文件表和 PPtr 追踪，没有按名称、低位、地址或排列猜测。

从当前 BlockV 校验四份完整 BLOCK 的大小及 MD5，再记录 SHA256。ENCR 目录只读取头部构建 CAB → BLOCK 索引，避免为找依赖复制约 16GB 缓存；选中目标后仍校验完整 BLOCK，不把目录存在等同于对象存在。

3 个源节点、2 个 PS、2 个 Renderer。父 PS 容量为 0、没有材质；子 PS 容量为 2、Burst constant 为 5、寿命约 0.3 秒。保留原值，不补材质或增加容量。UV 为 2×2、固定 frameOverTime=0.2，不能改成自动播放四格。

AS 与独立有界字节读取器对 4 个对象、4939 个字段值无差异；验证反向 GameObject 组件引用与非空 PPtr。历史 4.5 候选树与当前对象完整消费仅证明本样本结构兼容，不代表获得了 4.6 全量原生 TypeTree。

Unity 写后和 Prefab 重载复验包括曲线、渐变、数组、材质槽、原生 Renderer enabled、容量、源 TRS 及 GameObject active/layer。保存的源 Prefab 保留原 layer；隔离层只属于预览实例。

## Shader 与环境边界

实际 Shader 为 `miHoYo/CRP_Particles/Particle_OneChannel`，不是旧 UI Shader。材质关键词精确选择 Vulkan vertex blob 1 / fragment metadata blob 129；两段真实 SPIR-V 同存于 vertex 程序，fragment 条目没有独立代码。

SMOL-V 解码 → SPIRV-Cross HLSL → Unity 包装保留原算术。39 处绑定按 descriptor、块大小、偏移及类型核对。贴图从外置 `.resS` 解出，独立解码验证 PNG；资源文件也纳入 provenance。材质的数值、纹理变换、渲染队列、禁用 Pass、实例化和 GI 标记按源写入。

`experiments/static-battle-environment.json` 明确标作 **preview fixture，不是获取到的游戏环境**。全局强度、护眼等使用显式中性设置；空场景 `_HalfDepth01Tex` 为白色 normalized-linear-depth 远平面，far clip=100 并与相机强制校验。

因此本轮不证明 SoftParticle 遮挡交界、原游戏护眼/亮度、Stencil、后处理、CRP 管线或最终游戏色彩一致。不能把“编译并可见”写成“官方效果完整还原”。

## 修正与复验

只读反驳审计发现旧代码错误地把序列化 Infinity 编码直接 cast 为 API WrapMode，旧写后检查也重复了这个解释。当前 Unity 实测序列化对应：0→PingPong、1→Loop、2→ClampForever、3→Default。已改为显式转换，并把实际曲线再次序列化，直接核对原 Infinity 编码。

这也纠正了前一份 UI 粒子报告中“wrap 已保留”的过强表述。旧 UI 资源本轮未重导出；再次使用通用 Builder 导出时才应用修正。

原生战斗预览保留源固定种子 5455/0；只有启用自动种子的预览才使用确定性替代。旧 UI 显示适配器补上场景/脚本重载期间数组长度变化的检查，避免越界。

验证时间为 0.0333 / 0.0833 / 0.1667 / 0.25 / 0.5 / 1.4 秒：前四帧可见、无洋红，后两帧粒子数及变化像素为零。相同输入重采样、保存场景重新加载图像一致；另以默认 scene-mask 相机确认隔离。截取的是独立相机 RenderTexture，不是桌面截图。

未重启 MuMu、未注入、未改游戏进程或 RenderDoc，未置顶 Unity。原活动场景保持不变。测试记录、源哈希、图片、逐字段未映射项及 `evidence-ledger.json` 均位于：

`artifacts/StaticShaderSamples/HSR-4.6/battle-effect-source/`

## 尚未恢复

- `MonoEffect` / `MonoEffectPluginFollow` 的目标、枚举、偏移、缩放和时间控制行为；只保留源元数据，不模拟猜测的跟随逻辑。
- 原生自定义 Particle/Renderer 扩展，详见 Unity 报告的 unsupported；禁用模块中的残留参数不等于正在生效的缺失。
- 同版本游戏/RDC 像素基准、Material/Shader 到 EID 的运行时身份桥、完整 Timeline。

下一步宜选择一个额外含 Mesh 或第二种材质的同角色子特效，逐项扩展映射；本样本通过不表示复杂技能或全游戏均已支持。
