# HSR 4.5 上下文虚表归属实验

## 会话

- 客体：HSR 4.5.0，MUMU x86_64，包 `com.miHoYo.hkrpg`
- 会话：`HSR-4.5-MUMU_20260907_075424710`
- 模式：`--observe-transfers 1 --context-observer 1 --context-observer-all 1 --vtable-probe 1`
- native TypeTree：未启用

## 结果

本次记录了 10,771 个对象 Transfer、512 个上下文调用、16 个对象虚表探针和 1 个上下文
虚表安装事件。上下文 slot `0x0` 的原始实现是：

```text
context vtable: 0x400023a73518
original target: 0x400022d839e0
```

修正 `/proc/self/maps` 列解析后，地址归属得到确定证据：

| 地址 | 映射 | 结论 |
| --- | --- | --- |
| `0x400022d839e0` | 客体 `lib/arm64/libunity.so`，`r--p` | 被观察的原始上下文实现 |
| slot `0x0` 当前目标 | `libTypeTreeRipper.so`，`r-xp` | 本工具的转发 Hook |
| 首个调用返回地址 | `/system/lib64/libhoudini.so`，`r-xp` | MUMU 的 ARM64 转译层返回地址 |

上下文调用携带的对象类型 ID包括：

- `Light`：108
- `MeshFilter`：33
- `MeshRenderer`：23
- `Animator`：95

少量 `Light` 调用在上下文切换窗口中没有对应的已观测对象槽位，因此 `typeId` 为 `-1`；
这被保留为缺失证据，没有用类型名称反推 ID。

这些 ID 是运行时观察到的 Unity ClassID，不能直接当作字段 ID、PathID 或资源身份。
参数与 Transfer 内存之间仍只验证到以下关系：

- 参数 0 在部分调用中等于对象 `+0x28` 的低 32 位；
- 参数 1 等于 Transfer `+0x58` 的低 32 位；
- Transfer `+0x5c` 的低 8 位保持为 `1`。

## 当前边界

- `unity.typetree` 事件仍为 0；`runtime/typetree/release.ttbin` 仍是 31 字节空树。
- 上下文调用已被证明是真实调用链，但参数语义尚未被证明为 TypeTree 节点、字段名或
  资源 PathID。
- 客体 APK 的 SHA-256 与本地静态分析输入一致，但运行时原始代码字节不能直接用静态
  ELF 的同地址反汇编替代；下一步需要以运行时字节或精确映射关系为依据。

## 原始证据

### 082410160 实机回归

会话 `HSR-4.5-MUMU_20260907_082410160` 正常结束观察，但目标未完成：

- `unity.transfer` 10748 条，上下文调用 512 条。
- 布局和虚表观察分别 16 条，ABI 诊断 8 条。
- `unity.typetree` 和 `unity.typetree-candidate` 均为 0。
- `release.ttbin` 为 31 字节，不能作为字段级 TypeTree 交付。
- AS Shader、Material 和显式关联均为 0。
- 4 条事件含新的 `entryBlockBl_` 标签，旧 `directBl_d` 标签为 0。

因此本轮只验证了调用证据采集修正生效，不能证明资源身份桥或字段解码成功。

后续静态候选是 Light 对象槽位 `+0xA8`，而非当前观察的 `+0xA0`。
旧会话记录的该入口前 16 个指令字与正确映射后的 ELF 一致；静态代码中可见
字符串 `Base`、节点写入调用以及 32 字节节点步长操作。这些仅支持继续核验，
尚未证明该函数在本次观察中被调用，也未证明完整 ABI。不要据此直接启用
未验证的 native TypeTree 调用。

### 083109284 单变量被动实验

沿用 082410160 的 CLI 参数和同一 runtime 构建，仅将
`--observe-vtable-offsets 0xa0` 改为 `0xa8`。观察最长 70 秒，
native TypeTree 未启用。客户端为 4.5.0，但前台状态未知，不能声称已经进入场景。

最终状态 `completed_observer_only`：16 条观察器安装记录、16 条虚表记录，
没有实际 Transfer 回调，也没有字段节点和显式关联。实验已结束，不在后台监听。
该结果只排除了“当前启动观察窗口会自然调用 +0xA8”的预测，不能排除其他
游戏状态下触发，也不能据此断言该槽位不是节点生成函数。

离线比较工具现已包含虚表槽位窗口，并把缺失指令字标为 `incomplete_capture`，
而不是按零比较。082410160 的 +0xA8 首 64 字节中，Light、Material、MeshFilter、
ParticleSystem、ParticleSystemRenderer、Camera 与 ELF 一致；Animator 和
MeshRenderer 不一致。Material 静态入口同样引用 `Base` 并调用 `0x23c77a4`。
这不是完整函数、完整 ABI 或字段语义验证。

下一阻塞：需要确认候选生成入口的完整运行时实现、调用所需上下文的初始化与
生命周期，或找到自然调用它的确定流程。不能以 A0 的序列化上下文替代 A8 的
生成上下文，也不能因被动观察无回调就直接执行未知 ABI。

### 旧窗口比对结果

### 候选 ABI 的逐字段证据

本地 RVA `0x23c77a4` 的前 1024 字节在 080610608 会话中与客体一致。
旧采集器把它归到 A0 调用链的标签错误，但已读取的代码字节仍可比对；
Light 和 Material 的 A8 首块中可以直接看到调用该地址的 BL。
这证明代码中的直接调用边，不证明本次实验执行过该调用。

| 上下文偏移 | 指令位置 / 行为 | 当前证据边界 |
| --- | --- | --- |
| `+0x40` | `0x23c77cc` 读取后判空 | 当前父节点的数据对象指针候选 |
| `+0x48` | `0x23c77dc` 读取，按 32 字节步长寻址 | 父节点索引候选 |
| `+0x30` | `0x23c7858` 在无父节点分支读取，再两次解引用 | 根数据包装对象，必须已有有效节点存储 |
| `+0x38` | `0x23c7820` 有父节点分支读取 | 与父节点相关，完整语义待证 |
| `+0x6C` | `0x23c78bc` 读取并递增，写到节点 `+16` | 递增节点索引 |
| `+0x50/+0x58` | `0x23c78e0/0x23c78f0` 计算地址差并校验范围 | 对象起点和范围上界候选 |
| `+0x08` | `0x23c78b4` 测试标志位 | 仅证明该处标志字节，未证明完整宽度 |

节点结构可见：`+2` 层级、`+4/+8` 接收两个字符串操作的输出、
`+12` 大小、`+16` 索引、`+20` 标志；步长为 32。
字符串操作 `0x23b02ec` 本身尚未完整核验，不能凭两个输出位置就生成字段树。

现有初始化并非完全空树：`TypeTreeShareableData` 构造器已经创建一个根节点，
标准 `GenerateTypeTreeTransfer` 构造器也设置了 generation flags。
所以不能把失败归因于“忘记创建根节点”并再次打补丁。
尚缺证据的是完整结构、Unity 分配器/扩容约定以及构造与销毁协议；本轮未改布局，
未启用 native 调用。`dumper.hpp` 的说明已改为部分证据，撤去完整 ABI 已确认的暗示。

### 旧窗口统计

### 字符串写入核验

`0x23b02ec` 前 1024 字节在 080610608 的 ParticleSystemRenderer 代码窗口中
与 ELF 一致。可见逻辑把公共表内地址减去 `0x625740` 后置 `0x80000000`，
否则在数据对象 `+0x20` 的局部字符串数组查找或追加，最后输出偏移。
局部数组大小在数据对象 `+0x30`，编码容量在 `+0x38`，比较前右移一位。
这里的偏移来自该版本指令，不是跨版本配置，也不应硬编码为通用解码入口。

ELF 的 `[0x625740,0x625bc1)` 字节并非明文公共表。不能把静态表直接替代
运行时明文，也没有引入猜测性的解码密钥。当前 `GetCommonStringBuffer` 搜索
运行时 `AABB\0AnimationClip` 模式，但旧记录没有保存结果。

为补齐这个证据缺口，观察器安装事件在 layout evidence 开启时新增
`commonStringBuffer`、最多 32 字节的 `commonStringPrefixBytes` 和
`commonStringEvidence`。`prefix_readable` 仅表示前缀可读，不表示完整表正确。
读取逐字节检查，不要求额外的整页可读空间，不改变实际解码路径。
Android x86_64 与 .NET 8 构建通过；尚未进行新版本实机采集。

### 历史窗口数量

### 扩容目标的静态边界

RVA `0x22702c8..0x22703cc` 可静态反汇编，尚无该目标的运行时字节比对。
它读取数组 `+24` 编码容量，检查最低位，并读取 `+8` 标签、`+16` 数量、
`+0` 数据地址。最低位置位时在 `0x2270354` 进入分配路径，随后以旧数据地址、
新地址和 `count * elementSize` 调用 `0x29a9f20`，更新指针及编码容量后返回。
该分支没有直接以旧数据指针调用释放函数；内部被调用函数语义仍需核验。
这与 Ripper 的 reference-bit 设计一致，但不是客体 allocator 已验证的结论。

LLVM 将 `0x29a9f20` 标注为 `memset@plt`，而这里参数明显为源/目的指针和长度，
与先前字符串追加路径相同。符号标签不可当作语义证据，尤其不能据此声称它清零
或复制了数据。后续应核验实际导入目标，不依赖现有 PLT 名称。

最小下一证据需求保持具体：上述扩容目标的运行时字节及其两个分配调用目标，
并确认 `0x29a9f20` 实际实现。尚不需要重复整段启动被动采样，也不应为绕开
扩容问题直接塞入超大缓冲区并声称 ABI 正确。

### 085321254 指定 RVA 实机回归

会话进入前台并观察到 ParticleSystem、ParticleSystemRenderer，最终状态
`completed`。记录 10746 条真实 A0 Transfer、512 条上下文调用；字段树仍为空。
指定 RVA `0x22702c8` 在 7 类布局事件中各保存 1024 字节，按同次 maps 映射与
ELF 比较均为 `exact_bytes`。因此扩容目标的静态指令可用于本版本后续 ABI 分析。

该实现确认数组对象字段为数据 `+0`、标签 `+8`、数量 `+16`、编码容量 `+24`。
reference bit 置位时会分配并复制，不直接释放原外部数据；这与当前动态数组
外部缓冲区标记一致。被调分配器的完整生命周期仍没有单独验证。

完整 Light A8 函数还访问 Transfer 的 `+0xB8/+0xC8/+0xD8/+0x108`。
旧 SR candidate 仅有 0x78 字节并单独位于栈上，执行时会越界。现已将 candidate
placement-new 到原有 0x400 字节零初始化 guard storage，避免已确认的栈越界；
Android x86_64 与 .NET 8 构建通过，0 警告、0 错误。native 开关保持关闭。

为避免下一实验产生假阴性或误跑多类，CLI 的 native 预检已收紧：必须显式提供
单个 `--guest-class-ids` 和 `--vtable-offset`，并必须传
`--vtable-probe 0`。原因是 dumper 的 vtable probe 分支会在 native 调用前
`continue`；Profile 默认值为 1 时，旧 CLI 会正常结束但根本不执行生成调用。
该变更经 .NET 8 Release 构建验证，0 警告、0 错误。

第一次 CLI 负向测试发现上述校验原本位于 `CaptureGuestTypeTree` 内，执行顺序晚于
MuMu shutdown/launch，因此错误参数也会重启模拟器。现已把同一 native 预检移到
所有会话目录创建和 MuMu 控制之前，并保留采集函数内的二次校验。回归结果：

- `--guest-class-ids 108,21`：退出码 1，拒绝多 ClassID。
- `--guest-class-ids 108 --vtable-probe 1`：退出码 1，拒绝 probe/native 冲突。
- 两次拒绝后 MuMuNxMain 保持 PID 40588，启动时间仍为 17:16:58。

因此授权后的有效实验参数必须包含 ClassID 108、`--vtable-offset 0xa8`、
`--vtable-probe 0`、native layout 和双重风险确认。该结论只验证参数门，不代表
GenerateTypeTree ABI 已验证或字段树已取得。

## 受控 native TypeTree 结果

在同一 HSR 4.5.0 客体、Unity `2019.4.34f1` 和 `+0xA8` 槽位上，三个互相隔离的
单 ClassID 实验均正常返回，MuMu 客体未崩溃。所有结果均由 `my-hook-tool typetree
--strict --full` 复验，不使用名称、地址、顺序或 RenderDoc ResourceId 做跨来源推断。

| 类型 | ClassID | 节点 | 字符串字节 | ttbin SHA-256 | 严格校验 |
| --- | ---: | ---: | ---: | --- | --- |
| Light | 108 | 187 | 1323 | `0f3a5bfed58c580a50a5cf500af549fe28e5ee437a554d9f8ea5a3bc36fbf04a` | 通过 |
| Material | 21 | 76 | 295 | `4b5f1755748dbe1eccf95209b4618a106e693ab6aa0540efb7ec8bfcdbff0ba3` | 通过 |
| Shader | 48 | 1551 | 2288 | `848a04f18f9ad53a98e44bbcb117fb160e2f12a9706a7bd9bf084cb1d69b3026` | 通过 |

关键证据：Material 字段树包含 `m_Shader.m_FileID`、`m_Shader.m_PathID` 和
`m_ShaderKeywords`；Shader 字段树包含 `m_Name`、`m_ParsedForm`、属性、SubShader、
Pass、状态及程序结构。字段树解决序列化解码，但它本身不证明运行时对象与
`sourceFile#PathID` 或 RenderDoc EID 的对应关系。

对应会话：

- `HSR-4.5-MUMU_20260907_093332508`（Light）
- `HSR-4.5-MUMU_20260907_093700601`（Material）
- `HSR-4.5-MUMU_20260907_093840536`（Shader）

下一项必须新增的证据是同一会话中的资产反序列化身份记录：
`(runtime object, source file, PathID)`。随后必须沿实际 Material/Shader 绑定调用把
同一对象身份传递到 RenderDoc 捕获边界并记录共享 EID。当前 `unity.transfer` 的
对象内存快照不包含可验证的来源身份，不能替代该观测点。

真实 A0 Transfer 的 `+0` 是虚表，但当前已核验的 Light A8 函数没有读取
Transfer `+0`；这不能证明它的全部被调函数都不依赖虚表，也不能证明普通
序列化 Transfer 虚表可用于生成上下文。下一步若要实际调用，只能作为会导致
客体游戏进程崩溃的受控 native 实验，不能包装成安全观察。

### 节点扩容入口的运行时核验

节点写入函数的容量不足分支在 `0x23c79c4` 调用 RVA `0x1733284`。
该 RVA 的静态字节不可直接用作实现。080610608 已捕获的运行时窗口用
Capstone 5.0.7 ARM64 离线反汇编，入口实际只有三条指令：

```text
mov w1, #0x20
mov w2, #8
b   0x4000232702c8
```

因此下一依赖是该会话目标地址对应的 RVA `0x22702c8`，不能把后面的
`0x400022733290` 当成扩容函数主体；那已经是另一函数。
两个常量与节点步长、对齐需求相符，但参数语义和目标函数分配/释放协议仍待证。
旧比较报告中没有该尾跳目标的窗口。尚不能验证 `dynamic_array` 的引用位
是否保证 Unity 不释放外部分配内存。本轮未改 allocator、未执行 native 调用。

### 084158588 公共表与入口回归

runtime SHA-256：`dc5c2cafb76347f39f403cea2dece673c42758ae5a41eb15382a11029c33b9bd`。
会话结束于 `completed_observer_only`，32 条事件，未取得实际 Transfer 或字段节点。
16 条安装事件均保存了同一个可读公共表前缀：
`AABB\0AnimationClip\0AnimationCurv...`，地址为 `0x400021625740`。
这排除了本轮“公共表搜索返回空”的假设，但仅检查前 32 字节，不代表完整表验证。

安装事件已有 `installedOriginal` 512 字节窗口。使用本会话虚表探测的唯一
`moduleBase`，把入口换算成 RVA，再按本地 ELF64 PT_LOAD 映射比较：

- Camera、Material、MeshFilter、Mesh、Shader、AnimationClip、Light、LineRenderer、
  ParticleSystem、ParticleSystemRenderer：128 个指令字全部一致。
- GameObject、MeshRenderer、Texture2D、Animator、SkinnedMeshRenderer、PlayableDirector：
  128 个指令字全部不一致，不将静态实现直接当作其运行时实现。

这是 PT_LOAD 假设下的字节比较，不是同次 runtime maps 验证；未采用其他会话的
绝对地址进行身份关联。代码窗口不是函数边界，不能据窗口范围推导调用可达性。
Light 已核验的入口至返回范围处于一致窗口内。下一步仍需初始化/扩容协议证据，
不能因为入口字节一致就认定原生生成上下文可安全构造。

`tools/Compare-RuntimeCode.ps1` 对 080610608 会话的代码窗口按 maps
映射比对静态 ELF：303 个按类型、地址、字节去重的窗口中，263 个完全一致，
40 个不同。窗口数量不是函数数量，更不是已验证调用数量。

Light 原始入口 `0x400022cbd150` 的 512 字节窗口完全一致。
该函数在 RVA `0x1cbd2c8` 尾跳转，后续 RVA `0x1cbd390` 是另一段函数代码。
旧采集器把入口之后固定 1024 字节内所有 BL 递归解析为调用关系，因而错误地
把 RVA `0x1cbd3c4` 调用 `0x23c77a4` 标记为原始入口的调用。
现有 `directBl_d*_from_*` 标签不能作为函数可达性证据。

采集器已改为仅记录入口基本块内的直接调用，遇到分支和返回即停止。
完整的多基本块控制流仍待实现；原始字节窗口保留供离线检查。
同时修正了短跳板 B 的指令掩码，避免把 BL 误当成无条件跳板。
这些修复不代表字段级 TypeTree 或资源身份桥已经取得。

### 后续复核：对象虚表与上下文窗口必须分开

复核 `HSR-4.5-MUMU_20260907_080610608` 的 `unity.vtable-probe`
事件得到以下原始对象槽位（取 `originalTarget`，排除 Hook 替换地址）：

| 类型 | 对象虚表 | 对象 +0xA0 原始目标 |
| --- | --- | --- |
| Material | `0x400023a82480` | `0x4000232086cc` |
| Shader | `0x400023aa0de8` | `0x4000238b9114` |
| Light | `0x400023a718f8` | `0x400022cbd150` |

此前对 `0x400022d9e990` 的静态比对取自上下文地址
`0x400023a73518 + 0xA0`，不能用于确认上述对象的 Transfer 入口。
读取虚表之后的一段可读内存也不能证明整段都是该类的虚表。
上游 `Object.hpp` 的槽位布局只能提供候选，不能单独证明当前客体的函数语义。
撤回此前“+0xA0 已确认是 GenerateTypeTree”的结论，保持入口语义未验证。

本地 ELF64 程序头复核：代码 LOAD 的文件偏移为 `0x8ec600`，
虚拟地址为 `0x8f0600`。文件偏移和 RVA 存在差值，比较必须明确采用
PT_LOAD 或实际 maps 映射，而不能直接以 RVA 作为文件偏移。
当前上下文回调的 maps 推导文件偏移为 `0x1d7f9e0`；该位置的静态字节
与记录的运行时字节不同。差异原因仍未证实，不能直接归因为 MuMu 转译或解密。

```text
artifacts/HookSessions/HSR-4.5-MUMU_20260907_075424710/runtime/events.ndjson
artifacts/HookSessions/HSR-4.5-MUMU_20260907_075424710/runtime/typetree/rtti-catalog.txt
artifacts/HookSessions/HSR-4.5-MUMU_20260907_075424710/runtime/typetree/release.ttbin
```

### 序列化身份双向映射候选

精确字符串 `i->first.serializedFileIndex != serializedFileIndex` 在当前
`libunity.so` 中只有一个精确 `ADRP+ADD` 引用，位于 RVA `0x23bf7f0`。
其所在函数 `0x23bf72c` 按传入的 32 位 `serializedFileIndex` 遍历并清理
两级树，不是身份写入点。

同一局部函数簇中的 `0x23befb0` 接收 16 字节键：读取键 `+0` 的低 32 位和
键 `+8` 的 64 位值，维护两张互逆树，并返回生成的 32 位值。全 ELF 只有
`0x23ad174`、`0x23ad194` 两个直接调用；调用方以 24 字节步长遍历记录，传入
前 16 字节并把返回值写回记录 `+16`。结合清理断言，当前只把三个观测字段
命名为 `serializedFileIndex`、`key64`、`runtimeValue32`；在独立证据闭合前，
不得把后两者声明为 PathID 或 InstanceID。

新增的 `unity.serialized-identity-map` 观察器默认关闭，并要求 Profile 同时提供
RVA `0x23befb0` 与首指令校验值 `0xd10283ff`。会话
`HSR-4.5-MUMU_20260907_100522870` 在 15 秒延迟安装时验证：运行时首指令匹配、
Dobby 返回 0、游戏保持运行，但登录阶段之后没有新调用。

会话 `HSR-4.5-MUMU_20260907_101200121` 尝试在 `libunity.so` 映射出现后立即
安装。该路径在 MuMu Native Bridge 尚未稳定时于
`InstallSerializedIdentityObserver()+721` 触发 SIGSEGV，未进入目标回调；提前
轮询线程已移除并重新构建。下一步应保持已验证的延迟安装，再主动触发新的
AssetBundle/场景加载以观测调用，不能继续把 Dobby 安装前移。

### 2026-09-09 安装闸门回归

会话 `HSR-4.5-MUMU_20260909_072214243` 出现两条完全相同的
`unity.serialized-identity-observer-installed` 记录，说明原先的
“检查后调用 DobbyHook”不是原子操作，存在同一目标被重复安装的竞态。
已在 `InstallSerializedIdentityObserver` 内增加互斥保护，并在
`HSR-4.5-MUMU_20260909_073211429` 回归：运行时只产生一条安装记录，
未复现此前的安装函数崩溃；同时没有产生 `unity.serialized-identity-map`。

因此当前结论是：安装路径稳定性已改善，但候选函数尚未在本次实际运行路径被
调用。下一实验必须在同一已注入进程中进入游戏并触发资源/场景加载；在此之前，
不能声称已经建立 `sourceFile#PathID` 或 Material/Shader 的运行时绑定。

### 2026-09-09 Native Bridge 架构边界

HSR 4.5 的运行库构建结果为 `ELF x86-64`，而静态分析的 Unity 目标映像和首
指令属于客体 `AArch64`。把 x86-64 Dobby 直接应用到客体地址
`0x4000233befb0` 会让 HSR 在该入口崩溃；关闭观察器的等时长基线没有复现，
因此该路径已判定为错误的跨架构 Hook，而不是可继续调参的 ABI 问题。

已加入架构保护：HSR Profile 标记 `arm64-guest` 后只输出
`unsupported-cross-architecture`，不再调用 Dobby。回归会话
`HSR-4.5-MUMU_20260909_075359481` 保留 TypeTree、无 HSR 崩溃。

同一进程中已能只读观察系统导出的 `NativeBridgeItf`：版本为 7，并取得回调
表地址；尚未调用其回调。下一阶段应验证 Native Bridge 的 ARM64 库加载/翻译
入口，再让 ARM64 构建的探针在客体指令集内工作，不能再从 x86-64 侧直接改写
客体 ARM64 代码。

### 2026-09-09 Native Bridge 回调表与加载实验

根据 Android NativeBridgeCallbacks 的版本 7 布局重新核对了回调槽位：

- callbacks[1] 是 loadLibrary
- callbacks[3] 是 isSupported
- callbacks[8] 是 getError
- callbacks[9] 是 isPathSupported，其参数是包含库的目录，不是 .so 文件

此前实验把 callbacks[9]、callbacks[10] 当作错误函数和路径检查函数，
得到的 pathSupported=false 不具备证据效力；更正后又发现，直接从当前
x86-64 注入模块调用 Native Bridge 回调会在 HSR 的 app_process64 内触发
SIGSEGV。因此这些回调现在改为分级实验模式，默认只读表，不调用未知 ABI：

- 模式 1：只调用 isSupported
- 模式 2：追加调用目录级 isPathSupported
- 模式 3：在前两项都通过后才调用 loadLibrary

该模式通过临时环境变量 MY_HOOK_NATIVE_BRIDGE_PROBE_MODE 启用，正常运行
不会调用这些回调。ARM64 探针也不再放入外部临时目录；实验代码会根据
pm path 解析当前 APK 的真实 lib/arm64 目录，避免硬编码安装路径。

当前仍未证明 Native Bridge 能从 x86-64 注入侧安全加载自定义 ARM64 库；
下一步应使用客体侧 ARM64 探针或 Android 原生加载链路验证，不能把宿主侧
回调崩溃误判为 TypeTree 或库 ELF 本身的问题。

公开入口的最小 A/B 结果：

- HSR-4.5-MUMU_20260909_090632097：只调用公开 NativeBridgeIsSupported
  时，TypeTree 目录正常生成，结果为 supported=false。
- HSR-4.5-MUMU_20260909_090821566：追加公开
  NativeBridgeIsPathSupported（传入 APK 的 /lib/arm64 目录）时，
  TypeTree 仍正常生成，结果为 supported=false、pathSupported=false。

因此当前证据表明：公开包装函数是可安全调用的，但 MuMu 当前的
Native Bridge 不接受该 APK 的 ARM64 库路径；直接表回调仍保持禁用，
因为独立实验会在 app_process64 内触发 SIGSEGV。身份桥下一步必须转向
客体侧 ARM64 加载入口或已被应用自身注册的 Native Bridge 命名空间。

### 2026-09-09 Native Bridge 公开入口与自定义库回归

为排除“只读到了错误路径”的可能性，重新构建后进行了两次不调用原始回调表的
最小实验：

- `nativebridge-public-safe-20260909/HSR-4.5-MUMU_20260909_104619256`：
  使用包内已存在的 `lib/arm64/libunity.so`。结果为
  `supported=false`、`pathSupported=true`、`handle=0`。
- `nativebridge-public-custom-20260909/HSR-4.5-MUMU_20260909_104855327`：
  将 ARM64 构建的 `libmyhook-typetree-ripper.so` 放入该包的真实
  `lib/arm64` 目录。结果仍为 `supported=false`、`pathSupported=true`、
  `handle=0`，且 TypeTree 目录照常生成。

两次结果说明：当前路径解析和目录访问均成立，但 NativeBridge 不接受这些库作为
可翻译加载目标；不能把失败归因于文件名、目录层级或 `loadLibrary` 调用时机。
同时，宿主侧直接调用 `NativeBridgeItf` 回调表仍被代码明确禁止，避免再次触发
跨 ABI 崩溃。下一步需要客体侧 ARM64 加载入口，或从已经由应用成功加载的 ARM64
模块中建立受控调用桥；继续在 x86-64 注入库里改变回调下标不会解决这个阻塞。

随后在 `nativebridge-host-dlopen-20260909/HSR-4.5-MUMU_20260909_105503034`
中测试了宿主 linker 的普通 `dlopen`。结果为 `handle=0`，错误明确为
`EM_AARCH64 instead of EM_X86_64`；该调用没有被转交给 NativeBridge。这个结果
排除了“直接从 x86-64 linker 加载 ARM64 探针”这一替代路径。该实验还修复了
诊断事件的 JSON 字符串转义，后续错误文本不会再破坏 `events.ndjson`。

### 2026-09-09 客体侧 ARM64 探针加载与身份调用回归

将观察器改为同时挂接 NativeBridge v7 的旧式 `loadLibrary` 槽位和 v3 的
`loadLibraryExt` 槽位后，捕获到了 HSR 实际加载的 ARM64 库及有效句柄。关键
记录包括 `libil2cpp.so`、`libmain.so`，其中 `loadLibraryExt` 的命名空间为
`0x5`。这证明 MuMu 的翻译加载链路可以被宿主侧安全观察，但不能用旧槽位替代
命名空间入口。

随后在 `nativebridge-extra-load-20260909/HSR-4.5-MUMU_20260909_110635413`
中，于真实的 `libil2cpp.so` 加载回调内调用同一个原始 `loadLibraryExt`，
加载 `/data/local/tmp/libmyhook-guest-arm64.so`，返回非零句柄
`0x79f74f870201`。客体库构造函数日志和增大的 TypeTree 输出共同证明 ARM64
探针已经执行，而不是只得到一个形式上的句柄。

身份观察器在客体 ARM64 库中安装成功，随后在
`guest-arm64-identity-auto-20260909/HSR-4.5-MUMU_20260909_111306923`
中产生 `1724` 条 `unity.serialized-identity-map` 记录。每条记录均包含：

- `serializedFileIndex`：运行时序列化文件索引；
- `key64`：目标函数读取的 16 字节键的高 64 位，当前仍保持原始命名；
- `runtimeValue32`：目标函数返回的 32 位运行时值；
- `caller`：客体 ARM64 调用点。

当前证据已经证明这是客体侧 PersistentManager 身份映射函数的真实调用，且
不再依赖名称、地址顺序或 GPU ResourceId。但 `key64` 到 AS `PathID` 的对应仍
需要用同一版本的 SerializedFile 对象表或反向 PersistentManager 查询闭合；在
该证据出现前，不把 `key64` 改名为 PathID。自动化逻辑已写入 `my-hook-tool`：
启用 `--serialized-identity-observer` 后会自动推送并在 `libil2cpp.so` 加载后
加载 ARM64 探针，不再需要手动设置额外环境变量。

### 2026-09-09 Transfer 字段证据

同一会话的 `unity.transfer-layout` 记录中，Material 的
`typeTreeObjectReferencedBuffers` 已包含客体侧真实的可读字段字符串。新的
`analyze` 输出会将这些内容写入 `runtimeFieldEvidence`，并保留类型、对象和缓冲区
索引作为证据上下文。HSR 4.5 自动会话目前得到 80 条记录，其中可重复看到：

- `UI/Default`
- `_MainTex`、`_SmoothMaskTexture`
- `_Brightness`、`_ColorGrading`、`_Contrast`、`_Saturation`
- `_BlendOp`、`_SrcBlend`、`_DstBlend`
- `_Stencil`、`_StencilComp`、`_StencilOp`、`_UseUIAlphaClip`

这些是 Transfer 缓冲区中的真实字符串，不是根据材质名称推断的字段；但当前仍没有
可靠的字段节点、偏移和类型三元组，因此分析状态为 `partial_observed`，不能将其
当作完整 TypeTree，也不能据此推导 Material 的 `sourceFile#PathID`。
