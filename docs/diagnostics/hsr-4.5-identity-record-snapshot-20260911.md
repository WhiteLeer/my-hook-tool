# HSR 4.5 身份写回记录快照实验

日期：2026-09-11

## 实验目的

在不改变 Unity 原始调用结果的前提下，对已确认的身份写回 caller 记录对象做
256 字节只读快照，检查该对象是否直接包含可解释的序列化引用字段。快照功能默认
关闭，不能替代 `sourceFile#PathID` 证据。

## 配置与结果

- 客体：HSR 4.5.0，Unity 2019.4.34f1，MuMu x86_64 Native Bridge
- 身份目标：`libunity.so + 0x23befb0`
- 已校验首指令：`0xd10283ff`
- caller 写回 site：2 个，分别位于 `0x23ad174` 和 `0x23ad194`
- caller/raw patch：安装成功
- 运行时记录：428 条
- `unity.serialized-identity-map`：0 条
- `unity.serialized-identity-post-store`：0 条
- `unity.transfer`：0 条
- TypeTree：只有 RTTI catalog，没有字段级 `.ttbin`

## 结论

本次证明了 patch 安装和会话输出链路，但没有进入会触发身份函数的资源加载路径，因而
没有产生记录对象快照。不能据此判断 `x28` 的语义，也不能把 `key64`、返回值或地址
改名为 `PathID`。

下一次最小实验应保持相同二进制和参数，只改变客体状态：进入实际游戏场景并触发
Material/Shader 加载；成功门槛是同一条身份调用同时产生 caller 记录、快照以及可由
独立序列化对象表验证的字段，而不是记录数量增加。
