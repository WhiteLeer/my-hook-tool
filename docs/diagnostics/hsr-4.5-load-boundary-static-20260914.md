# HSR 4.5 加载边界静态扫描

## confirmed

- 对当前 HSR 4.5 `libunity.so` 执行了只读 ARM64 扫描，输入文件大小为 46,105,336 字节。
- 扫描锚点为 `SerializedFile`、`PersistentManager`、`LoadObject`、`ReadPPtr`，窗口 192 字节，最多 200 个引用。
- 四个锚点均为 0 命中，未生成调用窗口，也没有可据此安装的新 Hook 位置。

## observed

- 当前二进制没有以这些明文符号保留加载边界名称；已有 `TypeTree`/`ALLOC_TYPETREE` 候选仍只是字符串引用和有限反汇编窗口。

## hypothesis

- 需要从已有的 4.5 运行时对象/Transfer 事件反向定位调用者或从序列化数据结构的实际读写路径入手，而不是继续扩大字符串猜测集合。

## blocked

- 本扫描没有产生可验证的 `SerializedFile/PersistentManager` 入口，因此未修改 Hook 偏移、未启动未知 ABI 调用。下一实验必须先取得同一对象加载调用中的原始 `sourceFile`、`PathID` 和运行时句柄三者，再进入 EID 对齐。
