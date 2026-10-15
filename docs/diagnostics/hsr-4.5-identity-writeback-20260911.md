# HSR 4.5 SerializedIdentity 回写证据

## 结论

对 HSR 4.5.0 的 `libunity.so`，运行时观测到的 SerializedIdentity 目标为 `0x23befb0`。静态 ARM64 指令分析确认该函数有两个调用点：

| 调用点 | 返回值回写 | 记录基址 | key 参数证据 |
| --- | --- | --- | --- |
| `0x23ad174` | `str w0, [x28, #0x10]` | `x28 + 0x10` | 调用前 `x1 = x28` |
| `0x23ad194` | `str w0, [x28, #0x10]` | `x28 + 0x10` | 调用前 `x1 = x28` |

因此可以确认：`key` 指向一个至少 24 字节的记录，SerializedIdentity 的 32 位返回值被写回该记录的 `+0x10`。运行时事件中的 `keyAddress` 按 `0x18` 递增，与该记录步长一致。

运行时读取 `key + 0x10` 必须注意时序：Hook 位于被调函数内部，读取发生在调用者执行 `str w0, [x28, #0x10]` 之前。因此该位置在 Hook 内仍可能保存旧值；不能把 `returnMatchesRecordValueAtHookReturn=false` 解释为 Unity 回写失败。

## 工具化方式

使用静态报告命令指定运行时观测到的函数目标：

```powershell
my-hook-tool typetree-static libunity.so `
  --anchor 'i->first.serializedFileIndex' `
  --identity-target 0x23befb0 `
  --output identity-static.json
```

报告中的 `identityCallsites` 只记录直接指令证据，不调用未知 ABI，也不会推断字段语义。

## 尚未证明的内容

- `serializedFileIndex` 的值已被直接读取，但它与 AS 的 SourceCAB 索引如何对应仍需同版本对象表或反向查找函数的直接证据。
- `key64` 仍保持不透明命名，不能仅凭位置、长度、重复模式或数值把它称为 PathID。
- `runtimeValue32` 与 Material/Shader 对象的关联尚未闭合；仍需捕获同一对象创建/回写路径，不能用地址或时间顺序代替关联。

## 运行时样本

会话 HSR-4.5-MUMU_20260911_065256696 已产生 1724 条 identity 记录和 518 条延迟探针。延迟探针中有 12 条在 1 ms 读取到与返回值完全相同的记录值，说明调用者回写路径在运行时确实发生。该探针只覆盖有界样本，不能据此声称所有记录都已关联。
