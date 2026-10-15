# 统一验收

`verify` 是只读验收入口，不会改写 AS、TypeTree、运行时 Hook 或 `.hook` 输入，
只生成一个机器可读的 `my-hook.verification.v1` 报告。

```powershell
my-hook-tool verify `
  --as-index '.\as-asset-index.json' `
  --typetree-report '.\composite.typetree-report.json' `
  --analysis '.\runtime.analysis.json' `
  --profile '.\profiles\HSR-4.5-MUMU.json' `
  --hook '.\capture.hook' `
  --output '.\verification.json'
```

也可以使用仓库内入口：

```powershell
.\tools\Verify-HSR-4.5-MuMu.ps1 `
  -AsIndex '.\as-asset-index.json' `
  -TypeTreeReport '.\composite.typetree-report.json' `
  -Analysis '.\runtime.analysis.json' `
  -Hook '.\capture.hook' `
  -Output '.\verification.json'
```

## 状态和退出码

- `verified` / `0`：所有请求的证据均通过。
- `invalid` / `1`：输入 schema、身份、路径或绑定不合法，工具拒绝验收。
- `partial` 或 `blocked` / `2`：部分覆盖，或缺少运行时证据；不能当作完成。

验收分为四个核心检查：

- `asIdentityAndPaths`：资产身份必须是 `sourceFile#pathId`，路径证据不能包含目录穿越。
- `materialShaderTextureBindings`：Material→Shader/Texture 两端必须在同一索引中存在，
  关系必须来自 AS 序列化引用。
- `typeTreeFields`：关键类型必须包含真实字段节点并通过层级/索引/大小检查；追加
  `--require-full-catalog` 时，报告中的全部 catalog 类型也必须覆盖。
- `runtimeIdentityBridge`：必须同时有非空实时 TypeTree、字段级运行时 catalog、
  版本匹配，以及带 Shader/Material `sourceFile+PathID` 和共享 RenderDoc EID 的
  `verified unity.draw-context`。

工具不会使用名称、顺序、数量、native 地址、RenderDoc ResourceId 或 hash 推导缺失
身份。离线 TypeTree 通过不等于客体运行时 TypeTree 已获取。
