# 三方对齐清单

`my-hook-tool align` 处理三类独立证据：

| 来源 | 能证明的身份 | 不能直接证明的身份 |
| --- | --- | --- |
| RenderDoc `capture.json` | EID、阶段、ResourceId、ShaderHash、InterfaceHash、VariantHash、绑定资源 | Unity Shader 名称、Material PathID、原始资源路径 |
| AnimeStudio 报告 | Shader/Material 名称、PathID、TypeTreeHash、关键字、材质参数、贴图引用 | 某次 GPU Draw 使用了哪个 Material 或 GPU 变体 |
| 运行时 `.hook` | TypeID、类型名、原生对象地址、Transfer、vtable、诊断 | AS PathID、资源路径、RenderDoc EID |

输出格式为 `my-hook.alignment.v2`，包含：

- `rdc.shaders`、`rdc.events`、`rdc.stageUses`：从 capture manifest 选择出的可审计字段。
- `as.shaders`、`as.materials`：报告中的资产身份和材质绑定信息，不包含巨大的编译
  变体 blob。
- `runtime.types`：按 `(typeId, typeName)` 聚合的直接记录统计。
- `analyze.identityBridge`：运行时 `unity.draw-context` 身份桥的覆盖状态；只有
  记录包含完整的 Shader/Material `sourceFile+PathID` 和共享 EID 时才会显示验证通过。
- `asIndex`：可选的 AnimeStudio 资产索引摘要，包含 `SourceCAB#PathID` 身份、依赖
  路径和已校验的 Material→Shader/Texture 绑定；它不会把 AS 身份自动映射到 RDC。
- 若 `--hook` 已由 `export` 生成并附带 `as/` 文件，`align` 会自动读取其中的
  `shaderReport`、`materialBindings` 和 `assetIndex`；显式参数优先。
- `asConsistency`：当提供 AS index 时，对比 index 与当前 report/bindings 的完整
  Shader/Texture 绑定集合；`exact` 才表示两份 AS 证据一致。
- `session`：汇总各输入中的 `captureSessionId`。`exact` 只表示至少两个来源
  提供了同一个会话编号；`partial` 表示只有一个来源提供；`mismatch` 表示来源
  之间出现不同编号；`not_available` 表示输入没有会话编号。
- `links`：`exact` 只表示同一来源内有明确引用；`explicit` 只表示 link-map
  通过了端点存在性校验。
- `unresolved`：跨来源仍缺少的身份桥接，以及下一步需要的证据。

`--link-map` 的 AS 端点必须使用完整的 `asShaderIdentityKey`，即
`<sourceFile>#<pathId>`；单独名称不能作为精确身份。可选的 `asShaderName` 仅用于
显示，并且必须与该身份对应的名称一致。

AS 资产身份

可先用 `as-index` 将 AS dependency manifest 与 Shader/Material 报告整理为可复用索引：

```powershell
my-hook-tool as-index .\as-output `
  --shader-report .\shader-report.json `
  --as-bindings .\unity-material-bindings.json `
  --output .\as-asset-index.json --force
```

索引中的 `paths` 是资产自身有证据支持的路径；`usagePaths` 是该资产被哪个
GameObject 相对路径引用的上下文，二者不能混用。每条 Material→Shader/Texture 绑定
必须能在同一个索引的资产身份集合中找到两个端点，否则索引拒绝用于对齐。
`pathValidation` 会统计路径类型并拒绝目录穿越段；绝对路径只作为来源证据保留，
不会被静默改写成可移植路径。

`as.identityDomain` 固定为 `serialized-asset:sourceFile+pathId`。每个 Shader、
Material 和纹理引用都会保留 `assetIdentity.key`，格式为
`<sourceFile>#<pathId>`；Material 的 `shaderIdentity` 和纹理的
`assetIdentity` 是来自 AnimeStudio 序列化引用的精确关系，不是按名称匹配。
`identitySummary` 会报告完整身份、重复键和缺失身份的数量。

如果 AS 报告提供了生成文件路径，`as.shaders[].path` 会原样保留；它表示本次
导出的 ShaderLab 文件位置，不是游戏包内的原始路径。Material 中的
`shaderIdentity` 仍是实际序列化 Shader 引用，优先级高于这些展示路径。

`source.originalPath` 只表示 AnimeStudio 读取过的源文件证明，通常是本机的
Block 路径；它不是游戏运行时解析出的 Unity 资源路径。跨机器使用时应以
`sourceFile + PathID` 为身份，不能把绝对路径当作可移植资源路径。

运行时 TypeTree

`.ttbin` 的字段解码只接受文件中真实存在的节点。可单独检查字段覆盖率：

```powershell
my-hook-tool.exe typetree .\release.ttbin `
  --catalog .\rtti-catalog.txt `
  --required-type Shader `
  --required-type Material `
  --required-type ParticleSystem
```

报告状态 `complete_for_requested_types` 只表示指定类型均有字段节点；它不表示
整个 Unity 运行时的所有类型都已覆盖。当前 HSR MuMu 客体会话生成的空树应显示
为 `decoded_empty`，不能拿来做字段解码；必须使用同一客户端/引擎版本、且报告
为非空的 TypeTree 文件。

使用 `--full` 时，报告会在每个实际节点上增加由序列化 `level`/`name` 计算出的
`parentIndex` 和 `fieldPath`，并输出 `fieldIntegrity`。其中 `refTypeHash` 仍以输入
文件为准；legacy StructDump 导入的 hash 没有来源时保持为零，不会补猜。

## 历史 HSR 4.4 样本（仅归档）

`frame3630` 的结果位于。该样本只用于保留历史边界，不参与当前 HSR 4.5
运行、注入或身份结论：

```text
artifacts/Alignment/frame3630-hsr-alignment.json
```

该结果包含 3 个 RDC Shader、1 个事件的 3 个阶段使用、1 个 AS Shader、24 个
Material，以及运行时 Hook 的 585 条记录。现阶段没有自动跨源关联；这是输入证据
中没有共同身份字段的结果，不代表这些 Shader 在游戏中一定不相关。

## 边界

不能使用以下信息作为自动链接依据：

- RDC 的 ResourceId 与 AS 的 PathID；
- 原生对象地址、vtable 地址与任何资源路径；
- Shader 或 Material 的相似名称、出现顺序或数量相等；
- GPU VariantHash 反推 Unity 关键字或材质赋值。

如果要补充关联，应由验证过的运行时 Hook 或人工 link-map 提供共同身份，并在
清单中保留来源和证明方式。
