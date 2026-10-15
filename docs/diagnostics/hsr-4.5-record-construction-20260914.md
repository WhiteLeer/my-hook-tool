# HSR 4.5 记录构造边界

## 范围

本记录只描述 `artifacts/analysis/HSR-4.5/libunity.so` 的窄范围 ARM64
反汇编，不给未知字段赋予 Unity 语义。

## 直接证据

- `0x18c6170` 调用 `0x23acd58`，调用参数包含一个对象指针、一个计数值以及
  多个栈上临时缓冲区。
- `0x23acd58` 从输入结构的 `+0x10` 读取计数，并以 `0x10` 字节步长读取输入
  元素。
- 该函数向输出记录以 `0x18` 字节步长写入：`+0x00` 的 32 位值、`+0x04` 的
  32 位值、`+0x08` 的 64 位值和 `+0x10` 的 32 位槽位。
- 随后的 `0x23ad164` 循环把每条 `0x18` 字节记录传给 `0x23befb0`，并将返回值
  写回 `record+0x10`。

## 语义边界

这些字段的布局和调用关系已确认，但没有直接证据证明它们是
`sourceFile`、`PathID`、Unity `InstanceID` 或 RenderDoc EID。当前不能用字段
位置、地址、处理顺序或低位匹配推导这些语义。

补充检查了 `0x23aca1c`：该函数读取输入结构的计数，消费前述记录并按记录中的
回调表逐项调用；检查范围内没有 SerializedFile、PathID 或运行时对象加载字段。
因此不能把 `0x23aca1c` 当作身份桥入口。

## 下一实验门槛

应在同一版本的 SerializedFile/PersistentManager 加载边界记录：原始
`sourceFile`、原始 `PathID`、返回的运行时对象句柄，以及后续同一对象的 Material/
Shader Transfer。只有同一调用链中出现直接共现，才可继续建立 RenderDoc EID 关系。
