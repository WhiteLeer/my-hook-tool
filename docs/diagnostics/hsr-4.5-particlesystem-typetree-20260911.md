# HSR 4.5 ParticleSystem TypeTree 实验

## 会话

- 客体：HSR 4.5.0，MUMU，`com.miHoYo.hkrpg`
- 会话：`HSR-4.5-MUMU_20260911_062134887`
- ClassID：`198`（ParticleSystem）
- Transfer 槽位：`+0xA8`
- vtable probe：关闭；native TypeTree：按已验证的单类型实验启用

## 结果

生成的运行时 TypeTree 文件大小为 `211883` 字节，工具严格解码得到：

- `ParticleSystem`：`4439` 个节点
- 字段完整性：通过
- 版本：HSR 4.5.0 / Unity 2019.4.34f1 Runtime

节点中已经包含实际的粒子参数和资源引用字段，包括：

- `startLifetime`、`startSpeed`、`startSize`
- `startColor`、`colorOverLifetime`、`colorOverTrail`
- `ShapeModule`、`EmissionModule`、`CollisionModule`
- `m_Texture`、`m_TextureUVChannel`
- `m_MeshRenderer`、`m_SkinnedMeshRenderer`

## 证据边界

本实验证明该版本的 ParticleSystem 字段树可以从客体运行时导出并严格校验，
但只覆盖 ClassID `198`。它没有证明任意类型都能用同一槽位，也没有建立
ParticleSystem 内部 PPtr 与 AS `sourceFile#PathID` 或 RenderDoc EID 的对应关系。
