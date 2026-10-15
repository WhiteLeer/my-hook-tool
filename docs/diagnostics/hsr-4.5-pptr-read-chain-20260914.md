# HSR 4.5 PPtr 读取候选链

## 证据范围

证据来自 `artifacts/analysis/HSR-4.5/libunity.so` 的静态 ARM64 反汇编，
以及 `serialized-anchor-static-20260914.json` 的锚点扫描。下文只记录指令和
参数流，不把候选字段直接命名为已确认的 Unity 语义。

## 观察到的参数流

- `0x16b8e74` 开始的函数是一个读取/转换结构的通用路径，使用带有读取游标、
  字节序标志和回调的输入对象。
- 在 `0x16b9088`，代码通过 `ADRP + ADD` 取得 `.rodata` 中的 `m_PathID` 字符串，
  随后从输入缓冲区读取 8 字节到临时结构。
- 在同一函数的 `0x16b9174-0x16b918c`，临时结构地址作为参数传给
  `0x23a94bc`；该临时结构的前部来自此前读取的 32 位字段，后部来自上述
  8 字节读取。
- `0x23a94bc` 从该结构的 `+0` 和 `+8` 读取两个值，并在 `0x23a95a0-0x23a95ac`
  重新组装后调用 `0x23bfbe8`；返回的 32 位结果写回调用者提供的槽位。
- `0x23bfbe8` 的入口随后以第一个值参与 32 位比较、以第二个值参与 64 位比较；
  命中时读取节点的 32 位结果，未命中时分配节点并写入两个键和结果槽位。这是
  “二键运行时索引表”的直接指令证据，但不是字段名称证据。
- `0x16b8e74` 还被多个相邻字段处理函数以 `(stream/context, field descriptor)`
  形式调用，例如 `0x16b94e4` 和 `0x16b9568`；这说明 `m_PathID` 分支位于
  通用字段读取链中，而不是一个单独的字符串查找例程。

## 可确认与不可确认

可以确认：存在一个以 `m_PathID` 为字段锚点、读取 8 字节并进入对象查找/解析
函数的静态调用链。不能仅凭此确认：前一 32 位值就是 `m_FileID`、后一 64 位值
就是 PathID、返回值就是 InstanceID，或该链必然属于某个 Material/Shader 对象。

## 下一步最小实验

只在同一版本上观察 `0x16b8e74`、`0x23a94bc` 或 `0x23bfbe8` 的真实调用，记录：
调用线程、输入结构原始字节、返回槽位、当前 Transfer 对象（若存在）、以及
同一对象的后续 Material/Shader Transfer。只有运行时直接共现并能与 AS 序列化
对象表核对时，才接受 `sourceFile#PathID` 语义。

## 2026-09-14 实机验证

会话 `pptr-read-chain-20260914-r1/HSR-4.5-MUMU_20260914_025650363` 使用同一
函数签名和首指令校验，仅把观察目标切换到 `0x23bfbe8`，未启用 raw/caller patch
或 native TypeTree。客体成功运行并产生 `65536` 条身份记录，其中 `65439` 条
来自运行时地址 `0x4000233a95b0`，按该会话模块映射对应静态调用点 `0x23a95b0`；
这与 `0x23a94bc` 调用 `0x23bfbe8` 的静态关系一致。会话同时产生 6 条 Material
Transfer、0 条 Shader Transfer；身份事件仍没有 sourceFile、PathID 或对象句柄。

因此本次实机结果确认“目标确实是 PPtr 候选解析链”，但没有闭合任何 Unity 字段
语义或 Material/Shader 对象桥。

## 2026-09-14 上下文关联复核

对上述会话的 `runtime/events.ndjson` 逐行解析得到：身份事件共 `65536` 条，
其中主要调用者为 `0x4000233a95b0`（`65439` 条），另一个调用者为
`0x4000228c78d0`（`97` 条）。身份事件的 `activeTransfer` 全部缺失；同一会话
的 Transfer 事件只有 6 条，且全部是 `Material`，没有 `Shader`。因此不能用事件
顺序、地址或相邻时间窗把两者配对。本次结果只证明两个观察器都生效，反而证明
当前记录点不足以建立同对象关系。

### 状态分类

- confirmed：`0x23bfbe8` 的调用链在客体内真实执行；Material Transfer 也真实执行。
- observed：身份记录与 Material Transfer 没有同一线程上下文中的直接共现字段。
- hypothesis：身份调用可能发生在序列化加载/缓存建立阶段，而 Material Transfer
  发生在后续阶段；该解释尚未由对象句柄证实。
- blocked：尚未取得同一个序列化对象的 `sourceFile + PathID`、运行时对象句柄、
  Material/Shader Transfer 三者的直接证据，因此暂不接受身份语义映射。

## 2026-09-14 native Transfer 最小实验

会话 `native-material-transfer-20260914-r1/HSR-4.5-MUMU_20260914_030507550` 仅限
`ClassID=21 (Material)`、`vtable +0xA0`，并启用一次 `native-on-transfer`。Transfer
观察器安装在已验证的 HSR 函数上，随后第一次 Material Transfer 进入回调；native
路径执行后客体退出，工具将会话标记为 `failed_guest_process_exited`。输出
`runtime/typetree/release.ttbin` 只有 31 字节文件头，解析结果为 0 trees / 0 nodes。

- confirmed：Material Transfer 观察器能触发；native 路径会导致本次客体退出。
- observed：失败发生在 native dumper 执行之后，没有生成字段节点或 Material/Shader
  直接身份关系。
- hypothesis：当前 native TypeTree 调用存在未证明的 ABI 或回调重入问题；不能仅凭
  现象判断是 vtable 偏移、参数布局或 dumper 内部状态导致。
- blocked：在没有精确 ABI 证据前，native-on-transfer 不可作为默认流程，也不能用
  这次失败结果生成可用 TypeTree。

失败日志给出了调用顺序：`ripper-logcat.txt` 的 282-287 行显示 Material 类型被
处理、Transfer 观察器安装到 `+0xA0`，随后调用该槽位；`android-errors-logcat.txt`
第 96-97 行记录客体线程收到 `SIGSEGV`，故障地址为 `0xdead1007`。这使“native
调用的 ABI/参数或重入条件尚未成立”成为明确的阻塞点；在补齐参数证据前，不应再次
执行同一 native-on-transfer 路径。

## 与 Windows 调用时序的静态对照

`source/windows/windows_impl.cpp` 的已存在实现先转发真实的
`GenerateTypeTreeNative(object, transfer)`，再在同一 Unity 主线程的 detour 返回点
执行 `RunDumperAndTerminate()`。而当前 Android `HookedTransferAtOffset` 在真实
Transfer 的原始函数返回后、但尚未返回其调用者之前直接调用 `RunGuestDumper()`；
该 dumper 又为每个 RTTI 类型构造一份局部 `GenerateTypeTreeTransfer`，并主动调用
候选 vtable 槽位。两者不是同一个调用时序，也不能据此证明 Android 的部分布局就是
真实 ABI。

因此下一次实现实验必须先只改变“调用时序”：在已确认的 Unity 主线程安全边界执行
一次同样的 native 调用，保持 ClassID、vtable 槽位、Transfer 布局和输出格式不变。
只有在时序变化后仍能无崩溃地产生可验证节点，才允许单独调整 Transfer ABI；不得
同时修改槽位和结构布局。

## 2026-09-14 Transfer 布局复核

安全会话 `pptr-read-chain-20260914-r1` 的 Material `unity.transfer-layout` 事件
记录了 `transfer+0x30 = 0x715c7f700c08`。该地址的快照首字段为
`0x400023a73518`（落在 `libunity.so` 的 vtable 区域），`+0x08` 为
`0x715c7f700d30`；这与一个带 vtable 的运行时上下文对象形状一致。对应的
`typeTreeDataBuffer` 与多个引用缓冲区来自该上下文，但尚未通过节点数组校验。

当前 `SrGenerateTypeTreeTransferLayout` 却把 `+0x30` 定义为 `TypeTree` 指针，
并在 native dumper 中填入局部 `tree` 的地址；局部 `TypeTree` 的首字段是数据指针，
不是上述运行时上下文 vtable。HSR Material 函数入口会从 `transfer+0x30` 取指针、
再取其首字段并间接调用，故该布局与实测访问不一致。真实 Transfer 快照中
`+0x50/+0x58` 也分别为指针样值，不能直接解释为当前结构中的 `ObjectPtr` 和
`ObjectSize`。

这条证据将下一步收敛为：先在不调用 native dumper 的安全回调中完整记录真实
Transfer 上下文及其调用参数，建立 ABI 字段表；不能通过把局部 TypeTree、地址或
邻近字段替换进结构来试错。

## 2026-09-14 目标函数有限窗口反汇编

对 `libunity.so` 的 RVA `0x22086cc` 做了 60 条指令的有限窗口检查。该函数的
直接访问模式是：

- 从 `transfer + 0x30` 取对象指针，再取该对象首字段并间接调用；
- 从 `transfer + 0x58` 读取 32 位参数；
- 从 `transfer + 0x5c` 读取标志位，并在标志成立时回写结果；
- 遍历 `transfer + 0x30` 指向对象中的记录区，每条记录步长为 `0x18`，并读取
  记录 `+0x14` 的 32 位字段。

这进一步确认 `transfer + 0x30` 在该 ABI 中是可调用的运行时上下文，而不是可以
直接替换成局部 `TypeTree` 地址的普通指针。该反汇编没有出现字段名、PathID 或
Material/Shader 语义，因此只能用于排除当前伪造布局，不能据此命名任何运行时字段。

## 原始缓冲区字段名检查

对同一安全会话中的 `unity.transfer` 与 `unity.transfer-layout` JSON Lines 及其
嵌入的十六进制/ASCII 缓冲区做了字面检查：`m_Shader`、`m_Name`、`m_Texture`、
`m_SavedProperties` 均出现 0 次，只有类型标签 `Material` 出现。因而当前快照
不是“字段字符串已经存在但离线解析器漏掉”，而是尚未取得字段名/节点字符串表；
不能从这些缓冲区生成字段级 TypeTree。

## 2026-09-14 离线对齐输入检查

对现有 HSR 4.4 `frame3630` 导出运行 `align` 时，工具拒绝该 `.hook`，原因是它
没有嵌入 `assetIndex`；随后尝试用 `sparkle_character_assets.json` 生成索引时，
`as-index` 又明确拒绝该文件，因为它不是包含 `dependency-manifest/Nodes` 的
AS manifest。故目前 HSR 4.4 导出虽有独立的 Material/Shader 绑定文件和
`sourceFile + pathId`，但尚未形成对齐器要求的统一 asset-index 输入，不能把
这次失败解释成跨来源身份不匹配。

## 2026-09-14 身份输入边界补充

对同一份 HSR 4.5 `libunity.so` 的直接调用窗口显示，`0x23a94bc` 的输入结构
来自调用者准备的两个字段：函数入口读取 `[x2]` 的 32 位值和 `[x2+8]` 的 64 位
值；随后 `0x23a95ac` 将栈上的键结构传给 `0x23bfbe8`，并把返回的 32 位结果
写回调用者记录。另一个调用者在 `0x23ad10c` 以 `0x18` 字节步长生成记录，再在
`0x23ad174/0x23ad194` 调用同一个索引函数。

这确认了“身份函数输入边界”和“记录结果回写位置”，但没有出现
`SerializedFile`、`sourceFile` 或 PathID 语义字段。故该边界可以用于下一次同对象
实验的记录点，但不能把两个输入字段命名为 PathID/InstanceID，也不能用返回值
与 AS 或 RenderDoc 的数值相等作为替代证明。

## HSR 4.4 现有导出边界

本地 HSR 4.4 `frame3630` 导出中的 AS 侧确实保存了 Material、Shader 和 Texture
的 `sourceFile + pathId`，例如 Material 的 `m_Shader` 与 `_MainTex` 引用；但同一
`.hook` 的 RenderDoc 部分明确标记为 `NeedsBinding`，并说明 GPU capture 不含 AS
名称，需额外的显式 link map。该产物因此证明了 AS 侧索引可用，却没有证明 GPU
Draw、运行时 Material 和这些 PathID 是同一对象。
