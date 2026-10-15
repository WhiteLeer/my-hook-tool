# HSR 4.5 身份证据审计

## 目的

`Audit-HSR45IdentityEvidence.ps1` 是运行时身份桥的只读验收门。它不根据名称、地址、日志顺序、数量、ResourceId 或 hash 建立关系，只接受同一条运行时记录中的直接字段证据。

## 使用

```powershell
.\tools\Audit-HSR45IdentityEvidence.ps1 `
  -Events '.\artifacts\...\runtime\events.ndjson' `
  -RenderDocEvidence '...\draw-replay-evidence.ndjson' `
  -Output '.\artifacts\evidence-audit\hsr45.json'
```

审计器要求候选记录同时具备：客体 draw 记录、活动 Transfer 上下文、Material 与 Shader 的 `sourceFile#PathID`，以及同一记录中的 RenderDoc EID。RenderDoc 事件文件只用于确认 EID 证据存在，不会把两个文件按顺序或时间拼接。

## 当前结果

2026-09-16 对既有 HSR 4.5 会话的负向回归：运行时 75277 条记录，RenderDoc EID 记录 179 条，完整同记录候选 0，结果为 `blocked`、退出码 `2`。这证明当前会话尚未闭合身份桥，也证明审计器没有把普通 Transfer/快照记录误判为成功。

## 下一门槛

必须以客体进程启动前加载探针的方式重新采集一次，并在真实场景中观察到 `guest.vulkan-draw-indexed`。只有该记录同时携带已验证的活动 Transfer 上下文和序列化身份，才进入与 RenderDoc EID 的显式关联实验；否则保持 `blocked`。
