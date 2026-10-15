# HSR 4.5 三槽位只读 TypeTree 排除实验

## 会话

- 客体：HSR 4.5.0，MUMU x86_64，包 `com.miHoYo.hkrpg`
- 会话：`HSR-4.5-MUMU_20260907_073010991`
- 运行时库：本地重新构建的 `TypeTreeRipper`，SHA-256 由启动器校验
- 模式：`--observe-transfers 1 --vtable-probe 1`
- 观察槽位：`0x98, 0xa0, 0xa8`
- native TypeTree：未启用

## 结果

| 证据 | 数量 |
| --- | ---: |
| 运行时 Transfer 事件 | 39,118 |
| `+0x98` Transfer 回调 | 28,369 |
| `+0xa0` Transfer 回调 | 10,749 |
| `+0xa8` Transfer 回调 | 0 |
| vtable 探针记录 | 16 |
| TypeTree 候选扫描通过完整校验 | 0 |
| `unity.typetree` 字段树事件 | 0 |
| `release.ttbin` | 31 bytes，空树 |

`+0xa8` 的 vtable 槽位确实被安装并出现在只读探针中，但本次客体没有通过该槽位进入
实际 Transfer 回调。因此不能用“槽位已安装”证明它是当前 TypeTree 生成路径。

`+0x98` 和 `+0xa0` 都是真实对象转移路径，但对两个槽位的 Transfer 内存执行了有界、
只读的候选扫描后，没有任何对象同时满足节点数组、字符串表、根节点和边界检查。当前
没有字段级 TypeTree 证据。

## 结论

1. `+0x98/+0xa0/+0xa8` 不能仅按相邻位置解释为 TypeTree 入口。
2. 当前安全观察器已经覆盖了三个由源码布局提出的候选槽位；继续增加相邻偏移没有证据基础。
3. 下一步必须定位实际的 TypeTree 生成调用及其 ABI，或在用户明确授权后进行一次隔离的 native
   调用实验。默认注入流程继续保持 native TypeTree 关闭。

## 原始证据

```text
artifacts/HookSessions/HSR-4.5-MUMU_20260907_073010991/runtime/events.ndjson
artifacts/HookSessions/HSR-4.5-MUMU_20260907_073010991/runtime/typetree/release.ttbin
artifacts/HookSessions/HSR-4.5-MUMU_20260907_073010991/runtime/typetree/rtti-catalog.txt
```
