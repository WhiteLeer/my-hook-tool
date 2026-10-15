# 工作效率诊断（2026-09-04）

## 结论

近期最影响排查效率的不是 TypeTree 或 Hook 代码本身，而是以下三个外围因素：

1. **MuMu 运行态内存膨胀**：本轮 `MuMuNxDevice` 工作集约 `7.7 GiB`，私有内存约 `15.9 GiB`，可用内存约 `7.9 GiB`。它会增加等待、切换和崩溃风险。
2. **分支与运行版本语义不一致**：仓库当前分支为 `SR-4.4-MUMU`，本轮实际使用的是 `HSR-4.5-MUMU` Profile 和 HSR 4.5.0 客体包。未在没有用户确认的情况下改名或重写分支，避免破坏未提交工作；后续每轮必须显式打印两者。
3. **历史实验产物干扰判断**：`HookSessions` 曾有 126 个会话目录，导致状态检查和人工检索都混入旧实验。

## 已采取措施

- 已停止本轮 Hook CLI 观察；没有运行中的 `my-hook-tool`。
- 保留最近 3 个会话，将其余 123 个会话移动到：
  `artifacts/HookSessions/_archive/20260904/`
- 已生成可恢复索引：
  `artifacts/HookSessions/_archive/20260904/INDEX.json`
- `tools/Show-HookStatus.ps1` 现在会排除 `_archive`，并显示 MuMu 的 working set 与 private memory。
- 没有强制结束 MuMu，因为这会打断当前登录态；若下一轮需要重新采集，应在启动前先重启 MuMu 以释放已增长的私有内存。

## 后续固定规则

- 每轮开始先运行 `tools/Show-HookStatus.ps1 -LatestSessionCount 3`。
- 一个会话只改变一个变量，并使用独立会话名。
- 没有新的字段级证据，不重复启动同一条观察链。
- 只把最近 3 个会话放在活动目录，其余移入按日期归档目录。
- 运行版本以 Profile、客体包版本和 ABI 三者同时记录为准，不以分支名推断。
