# HSR 4.6 真实 Prefab 小样本与下一断点

## 本轮目标和结论

从已确认的静态Shader样例推进到原始Prefab，优先小型特效，暂不并行铺开角色和Timeline。没有修改已验收的三个Shader程序，也没有将4.4资源改名充当4.6数据。

本轮尚未产生可验收的大特效预览。已将断点缩小到ParticleSystem字段布局，而不是用默认粒子或手写动画伪装成功。

## 当前来源

- 现有ADB服务实际监听5037；只读确认 `emulator-5554` 上 `com.miHoYo.hkrpg` 为4.6.0/118，没有重启、重连或注入。
- APK SHA-256：`07234d7e0f7fe0d083bab06a9504fea0604d6c80187f518774e55d3270e7d123`。
- APK中11个ParticleSystem、11个对应Renderer均无内嵌TypeTree。stock reader的小实验无法完整读取；后续库存工具不再对这些未验证类型强行解析，避免错误数组长度导致不必要分配。
- 另有3个MeshRenderer候选，为HalfSphere、SpotLightCone、icosphere等辅助几何，没有据此声称拿到角色。
- 本机完整PC资源目录仍为 `Games/SR_4.4`；不用于本轮4.6实机证据。

## 两个真实样本

| 样本 | 原始身份 | 文件级依赖 | 组件结果 |
|---|---|---|---|
| LinkGo | `c98c2440efc4cac43845e2e8db08100d#1` | 2文件 / 17684B | PS#4候选前缀216B，未解释8400B；Animator剩20B；没有已提取材质 |
| HeadFrame226003 | `ced5e536bf817f5488c8e270610660f2#4` | 19文件 / 20960848B | 10节点、3PS；历史外部树读到16172/8840和8980/8760，必须拒绝 |

第二组的 `globalgamemanagers.assets` 是6个split，按数字序拼接后验证声明长度。完成的文件级闭包只覆盖serialized external表，不能等同于资源语义、stream-data、脚本或运行时闭包。合并的globalgamemanagers有38894对象，无内嵌/引用字段树，没有额外补出粒子布局。

历史4.4外部树只作为反例候选，没有升级为4.6认证。它对部分Renderer能完整消费404/408/416字节，但单靠消费量和合理数值不能证明字段语义正确。

证据根：`artifacts/StaticShaderSamples/HSR-4.6/`。

- `prefab-candidates-safe/inventory.json`：源对象表/身份、层级字段与未解析状态。
- `prefab-linkgo/as-coverage-regression` 和 `prefab-linkgo/preflight.json`：修正后的216+8400字节统计及拒绝原因。
- `prefab-gold-header-verified/closure.json`：重新验证每份声明大小的19文件闭包。
- `prefab-gold-complete/preflight.json`：真实组件的越界、unsupported和拒绝结果。

## 工具修正

AS `EffectPrefabManifest` 原先对prefix+opaque尾输出0/0/0，容易被误读为无残留。现在分别填写ObjectByteSize、候选结构消费量和剩余量；保持partial状态，未改ParticleSystemParser的计算/搜索逻辑。

新增离线 `tools/audit_static_prefab.py`，在任何Unity操作前报告partial、缺参数、字节覆盖不一致、越界、unsupported和缺材质。它是诊断门，不是已完成的4.6导入器；当前版本语义未认证时不会授权导入。现有SR旧Importer仍会对partial补默认，本轮没有调用它，也未全局改变其历史行为。

`tools/extract_apk_dependency_closure.py` 支持split拼接，拒绝缺序号、重复条目、声明长度不符和v22扩展头错误；只允许新/空输出目录，拒绝混用历史inputs。设定128逻辑文件、64MiB输入预算，不是峰值内存承诺。

验证：AS .NET8 Release构建0错误，存在原有依赖/代码/FBX复制警告，未在本轮更换依赖；新增13项CPU回归通过，原18项Shader回归通过。两个真实包均被拒绝，未放入Unity，也未生成空/默认材质的预览。

## 当前原生资源目录

当前小型 `Start_AsbV_361d139e1e4d7dded99d3cdd4d8feccf.bytes` 已拉取并与M_Start_AsbV引用校验，1926148B、MD5相符。SRAM有8个相邻段，段0第4条在0x108处，以逐32位字节序转换后与已知 `33e698b3983400b700eb543162f21d36.block` 的实际MD5及238134B大小对应。

其余段保持opaque。已知7个CAB没有找到直接存储形式，因此尚无CAB→block或Prefab路径→block证明。没有把key64、索引序号或偶然整数相等解释成PathID。

## 接下来只推进一个断点

继续以LinkGo#4为最小布局实验：要求来自当前4.6、适用于其实际序列化条件的ParticleSystem schema/字段边界，逐模块核对，区分结构消费与原始尾。不能用历史树、扫描出的颜色块或合理默认值升级为字段恢复成功。

粒子/Renderer/材质绑定通过后，再导入原始Prefab、绑定已经恢复的真实Shader，并固定seed/时间做逐组件画面回归。之后扩展多粒子组合；角色完整依赖定位和Timeline脚本/绑定解码独立立项，不把匿名MonoBehaviour称作Timeline。
