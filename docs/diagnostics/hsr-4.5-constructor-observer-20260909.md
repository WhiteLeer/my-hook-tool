# HSR 4.5 构造观察实验

## 结论

候选 RVA `0x1c8cab0` 不是 TypeTree 构造器，而是内存分配器。运行时通过受保护的 `typeName` 字符串读取获得了以下真实标签：

- `ALLOC_DEFAULT_MAIN`
- `ALLOC_DEFAULT_THREAD`
- `ALLOC_GFX_MAIN`
- `ALLOC_GFX_THREAD`
- `ALLOC_CACHEOBJECTS_MAIN`
- `ALLOC_CACHEOBJECTS_THREAD`
- `ALLOC_TYPETREE_MAIN`
- `ALLOC_TYPETREE_THREAD`
- `ALLOC_PREFETCH_MAIN`
- `ALLOC_PREFETCH_THREAD`

因此该函数不能用于解码字段树，Profile 已明确标记为禁止复用。`typeName` 字符串读取本身是有效的，但它证明的是 allocator 标签，不是 Unity 序列化类型名。

## 运行证据

会话：

`artifacts/HookSessions/constructor-name-20260909/HSR-4.5-MUMU_20260909_123746305/`

- 事件行数：22503
- JSON 无效行：0
- 构造观察调用：10
- Transfer 布局事件：32
- 运行时字段证据：本次会话可由 `analyze` 生成报告；此前同链路已观察到 421 个可读字段字符串

事件写入过程中曾出现多实例并发追加导致 JSON 行交错，现已改为 `O_APPEND + flock` 的逐行写入并回归通过。

## 当前仍未解决

- 完整 TypeTree 节点及字段偏移尚未建立。
- `serializedFileIndex + key64` 尚不能证明为 `sourceFile#PathID`。
- Material/Shader 与 RenderDoc EID 的同帧身份桥仍未产生 `unity.draw-context`。
