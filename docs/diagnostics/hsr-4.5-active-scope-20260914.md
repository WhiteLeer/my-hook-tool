# HSR 4.5 活动目标切换

## confirmed

- 当前 MuMu 客体是 HSR 4.5.0，Unity 版本为 2019.4.34f1。
- 当前活动配置是 `profiles/HSR-4.5-MUMU.json`。
- 4.5 已有 TypeTree、Transfer 和身份观测证据，证据台账位于仓库根目录的 `evidence-ledger.json`。

## observed

- 工作入口使用 `tools/Start-HSR-4.5-MuMuHook.bat`。
- 当前本地分支原名仍带有 `4.4`，与实际客户端和 Profile 不一致。

## hypothesis

- 分支名本身不会改变 ABI 或运行时行为，但会误导后续实验选择，因此工作分支应改为 `HSR-4.5-MUMU`。

## blocked

- 尚未证明 Material/Shader 的运行时对象身份能直接映射到 Unity `sourceFile#PathID`，也尚未证明能映射到 RenderDoc EID。
- HSR 4.4 的历史证据不迁移为 4.5 结论；后续字段级结论必须以 4.5 客体的新事件和 ABI 证据为准。
