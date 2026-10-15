# HSR 4.5 native TypeTree 单类基线

## 实验约束

每次只请求一个 Unity ClassID，使用 HSR 4.5 客体、x86_64 Ripper 和已验证的
`vtable-offset=0xa8`。实验显式开启 native TypeTree ABI 安全令牌；没有启用
caller post-store 写补丁，也没有把地址、名称或数量当作资源身份。

## 结果

| 类型 | ClassID | `.ttbin` 大小 | 字段节点 | 字段完整性 |
| --- | ---: | ---: | ---: | --- |
| `ParticleSystem` | 198 | 211,883 bytes | 4,439 | valid |
| `Material` | 21 | 7,173 bytes | 76 | valid |
| `Shader` | 48 | 75,511 bytes | 1,551 | valid |

结果目录分别为：

- `artifacts/HookSessions/native-particlesystem-current-20260911-r3`
- `artifacts/HookSessions/native-material-current-20260911`
- `artifacts/HookSessions/native-shader-current-20260911`

每个结果都通过 `my-hook-tool typetree --required-type ...` 验证：请求类型覆盖
为 `1/1`，节点的类型名和字段名非空，父子层级和索引均有效，未发现无效字节大小。

## 边界

这些文件证明当前客体可以生成非空字段级 TypeTree，但不证明字段对应的运行时
值已经被读取，也不证明 `keyWord0`/`key64` 是 `sourceFile#PathID`。Material→Shader
以及 Shader/Material→RenderDoc EID 仍需同一对象、同一调用上下文的直接证据。
native ABI 参数与 Unity 版本绑定，换客户端版本不能直接复用本基线。
