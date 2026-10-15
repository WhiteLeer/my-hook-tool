# HSR 4.5 客体 libunity 静态审计

## confirmed

- 当前 MuMu 客体版本：HSR `4.5.0`，ABI：`arm64-v8a`。
- 实际加载库已从客体应用 native library 目录取出：
  `artifacts/analysis/HSR-4.5/guest/libunity-4.5.0-arm64.so`。
- 文件大小：`46105336` bytes；SHA-256：
  `3fd4436ae093c58a958ed78c144f198342b51ade083e7dae9f81d905fb2be68f`。
- 二进制字符串中存在 `m_PathID`、`m_FullPathID`、`TypeTree`、
  `PPtr<Texture3D>`、`m_Shader` 等序列化/资源字段名称。
- ELF 头确认这是 64 位 little-endian AArch64 (`e_machine=0xb7`)；仅有
  `.dynsym`（506 个条目），没有 `.symtab`，目标实现符号已被剥离。

## observed

- 这是从当前 4.5 客体取得的真实 `libunity.so`，不是 4.4 参考库或桌面端库。
- 字符串存在性不能给出函数地址、调用约定、字段偏移或对象生命周期关系。
- 动态符号表中没有导出名称包含 `Transfer`、`TypeTree`、`Serialized`、
  `Persistent`、`LoadObject`、`PathID`、`Material` 或 `Shader` 的入口；这不是
  “功能不存在”的证据，只说明不能靠导出符号直接安装通用 Hook。
- 以当前 4.5 客体库的已验证 RVA 反汇编：`0x23befb0` 读取参数 `x1+0` 和
  `x1+8`，在 `manager+0xa8` 的树结构中查找，命中后从节点 `+0x30` 返回
  32-bit 值；未命中时分配并插入一个新节点。
- 其直接调用者在 `0x23ad174` 与 `0x23ad194` 使用 `x28` 作为记录地址，
  调用前读取 `[x28+0x10]`，调用后执行 `str w0, [x28,#0x10]`，随后
  `x28 += 0x18` 遍历下一条记录。
- 同一调用者函数在 `0x23acf7c`、`0x23acf88` 等位置把上游临时数组的字段复制到
  0x18-byte 记录：`w21 -> +0`、`w28 -> +4`、`x22 -> +8`、`w25 -> +0x10`；
  上游数组由调用 `0x23a87a0` 填充。当前没有任何一条指令把这些字段直接连接到
  `SerializedFile`、`sourceFile` 或 `PathID`。
- 对 `0x23a87a0` 的调用窗口显示它接收多个动态数组/记录容器，按元素计数扩容并调用
  `0x23a84d4` 写入中间项；这进一步支持“内部索引/缓存构建”的解释，但没有提供
  序列化文件身份字段。
- 在确切 4.5 库中还发现第二个直接调用点 `0x18a98cc`。该调用者先初始化多组动态
  数组，调用 `0x23a87a0`，再按数组索引整理结果并释放临时容器；它也没有出现
  `sourceFile`、`PathID`、对象指针或 Material/Shader 语义。
- 对 `0x23acd5c` 做了按 4 字节对齐的直接分支与 `ADRP + ADD/LDR` 间接引用扫描，
  没有得到可精确计算到该 RVA 的静态入边。该结果只表示普通直接引用未发现，不能
  证明运行时没有调用；函数表、间接分发和 relocation 仍需运行时证据确认。
- 现有运行时事件仍只产生 TypeTree、Transfer 和身份查找观察；没有直接产生
  `sourceFile`、`PathID`、runtime object handle 或 RenderDoc EID。

## hypothesis

- 4.5 客体包含可用于对象序列化的实现，但相关加载边界可能位于 stripped native
  代码或客体自有资源加载层，不能由字符串匹配直接确定。
- 当前身份目标更符合“16-byte key -> 内部查找节点值 -> 记录写回”的缓存/索引操作；
  记录值的业务语义仍未知。要继续闭合身份桥，应追踪这些 0x18-byte 记录的生产者及
  `+0x10` 值的后续消费点，而不是继续扩大当前函数的观测字段。
- 记录生产者链目前只证明“临时数组 -> 内部记录 -> 索引查找”；字段语义仍保持不透明，
  不能据此命名为 PathID、InstanceID 或 runtime object handle。
- 下一轮静态工作应以这份确切二进制为输入，围绕已确认的 TypeTree/Transfer 调用窗口
  做交叉引用定位，而不是继续沿用旧版本 RVA。

## blocked

- 尚未从该二进制闭合 `SerializedFile/PathID -> runtime object` 的直接关系。
- 尚未证明 Material/Shader runtime 对象与 RenderDoc EID 的同一对象关系。
- 在没有确切交叉引用和 ABI 证据前，不增加新的 native Hook，也不把字符串、地址或
  ResourceId 当作身份映射。
- 当前下一静态定位点：`0x23ad100` 所在函数中构造/填充 `x28` 记录的上游，以及
  `0x23ad1ac` 之后消费写回值的调用链；优先检查 `0x23a87a0` 的输入输出是否出现
  可直接验证的序列化身份字段。当前已检查该入口，未发现这样的字段；下一目标改为
  追踪两个调用者传入的原始数据来源，仍只接受同一调用上下文中的直接证据。
- 静态地址匹配在此处已达到边界；下一实验应记录间接分发的实际函数表/调用上下文，
  并同时捕获序列化身份和 runtime handle，不能继续从缺失入边推导语义。
