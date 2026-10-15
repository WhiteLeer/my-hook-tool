# HSR 4.4 MuMu 闭环诊断

更新时间：2026-08-31

这份记录是当前 HSR-4.4-MUMU 流程的验收基线。它区分已经由输入证据证明的
结果，以及不能从当前客体 Ripper 产物推导的结果。没有共同身份字段的来源不会
通过名称、顺序、地址或数量被强行关联。

## 已验证

### AS 路径与身份

AS 资产索引使用唯一身份：

```text
<SourceCAB>#<PathID>
```

当前 Cerydra 样本结果：

```text
assets                 516
references             663
path evidence          768
absolute paths           0
path traversal errors    0
identity errors          0
```

`paths` 是资产自身有证据支持的路径；`usagePaths` 是引用该资产的 GameObject
相对路径，不能互换。索引文件：

```text
artifacts/regression/as-index/cerydra-asset-index.json
```

### Material 绑定

绑定来自 AnimeStudio 序列化引用，并按完整身份校验：

```text
Material -> Shader   24
Material -> Texture  23
binding errors        0
index validation      valid
AS consistency        exact
```

由 `export --as-index` 生成的 `.hook` 会复制同一份索引到 `as/asset-index.json`，
并在顶层 `as` 节点写入计数与校验状态。`align --hook` 可以自动读取这些嵌入文件。

### TypeTree 字段

当前版本化基线：

```text
Unity revision        2019.4.34
variant               Runtime
trees                 320
field nodes           21143
critical types        9/9
field integrity       valid
```

关键类型包括 Shader、Material、AnimationClip、SkinnedMeshRenderer、Mesh、
GameObject、Transform、ParticleSystem 和 ParticleSystemRenderer。完整关键类型报告：

```text
artifacts/regression/typetree/composite.typetree-report.json
```

## 当前阻塞

全 RTTI catalog 不是完整字段树：

```text
catalog entries       270
decoded with fields   190
missing               57
decoded empty         23
refTypeHash coverage   0
```

严格模式会拒绝这份全 catalog 报告，退出码为 2：

```text
artifacts/regression/typetree/full-catalog-strict.typetree-report.json
```

当前配置的目标是 HSR-4.4，但客体运行时观测到的客户端版本是 4.5.0。配置文件
中的 `observedClientVersion` 已保留这个差异，不能把 HSR-4.4 离线基线当作 HSR-4.5
客体实时字段证明。

当前运行时分析：

```text
live TypeTree       decoded_empty
draw-context        0
identity bridge     not_observed
exact cross links   0
```

诊断文件：

```text
artifacts/regression/diagnostic/adaptive-analysis.json
```

这意味着现有 TypeTreeRipper 能提供类型观察、Transfer 原始证据和 RTTI 名称，
但没有提供完整客体字段，也没有提供同时包含 Unity `sourceFile#PathID` 与
RenderDoc EID 的 `unity.draw-context` 事件。因此当前不能证明某次 RDC Draw 使用了
哪个原始 Unity Material/Shader，也不能补齐缺失的自定义 TypeTree 字段。

## 复现验收

统一验收入口（当前样本的结果为 `blocked`，因为运行时身份桥未观测到）：

```powershell
my-hook-tool verify `
  --as-index '.\artifacts\regression\as-index\cerydra-asset-index.json' `
  --typetree-report '.\artifacts\regression\typetree\composite.typetree-report.json' `
  --analysis '.\artifacts\regression\diagnostic\adaptive-analysis.json' `
  --profile '.\profiles\HSR-4.4-MUMU.json' `
  --hook '.\artifacts\regression\capture\mumu-host_sr_frame3630.hook' `
  --output '.\artifacts\regression\verification\critical.json'
```

该结果中 AS 身份/路径、Material 绑定和九类关键 TypeTree 均为 `verified`，
`runtimeIdentityBridge` 为 `blocked`。完整 catalog 的验收应改用下方的完整报告并
追加 `--require-full-catalog`，当前结果明确为 `190/270`，不能标记为完整。

本次回归报告：

```text
artifacts/regression/verification/critical.json
artifacts/regression/verification/full-catalog.json
```

关键类型必须通过：

```powershell
my-hook-tool typetree `
  '.\runtime\HSR-4.4-MUMU\typetree\hsr-2019.4.34-runtime-composite.ttbin' `
  --catalog '.\references\hsr-4.5-rtti-catalog.txt' `
  --required-type Shader,Material,AnimationClip,SkinnedMeshRenderer,Mesh,GameObject,Transform,ParticleSystem,ParticleSystemRenderer `
  --expected-revision 2019.4.34 --expected-variant Runtime --full --strict
```

全 catalog 必须明确失败，而不能被当作完整：

```powershell
my-hook-tool typetree `
  '.\runtime\HSR-4.4-MUMU\typetree\hsr-2019.4.34-runtime-composite.ttbin' `
  --catalog '.\references\hsr-4.5-rtti-catalog.txt' `
  --expected-revision 2019.4.34 --expected-variant Runtime --full --strict
```

预期第二条命令输出 `partial_for_requested_types` 并返回退出码 2。只有在新的
客体产物同时满足非空字段树、版本匹配，以及经过验证的 `unity.draw-context` 身份
字段后，才能把当前阻塞改为已解决。
