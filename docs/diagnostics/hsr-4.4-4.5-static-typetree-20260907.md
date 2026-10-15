# HSR 4.4/4.5 ARM64 静态 TypeTree 候选对照

## 输入

- HSR 4.5: `artifacts/analysis/HSR-4.5/libunity.so`
- HSR 4.4: `D:\Unpack_Workspace\HookSessions\runtime-package-evidence-20260831\hsr-4.4-apk\libunity.so`
- 工具：`my-hook-tool typetree-static`
- 扫描窗口：引用地址之后 `1024` bytes

## 结果

| 客户端 | 锚点总数 | 引用数 | 有 BL 窗口的引用 | 关键结果 |
| --- | ---: | ---: | ---: | --- |
| HSR 4.5 | 5 | 7 | 7 | `Source...` 两处；`ALLOC_TYPETREE*` 三处 |
| HSR 4.4 | 5 | 3 | 2 | `Source...` 两处精确引用，一处页级候选 |

HSR 4.5 的 `ALLOC_TYPETREE`、`ALLOC_TYPETREE_MAIN`、`ALLOC_TYPETREE_THREAD`
锚点没有在 HSR 4.4 产生对应引用。两版的 `Source and Destination Types do not
match` 均能定位到同一类比较/缓存函数簇，但其地址不同，不能硬编码复用。

## 结论边界

这些结果证明锚点扫描器工作正常，并能缩小 ARM64 静态分析范围；它们没有证明任一
`BL` 目标就是 TypeTree 生成器。当前仍没有非空字段节点，因此不能称为字段级
TypeTree 成功。下一步应在每个版本的候选簇内做离线函数语义比对，或取得明确的
运行时调用上下文；在 ABI 未确认前不调用候选函数。

机器可读报告：

- `artifacts/analysis/HSR-4.5/typetree-static.json`
- `artifacts/analysis/HSR-4.4/typetree-static.json`
