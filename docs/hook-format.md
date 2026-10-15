# `.hook` format

`.hook` is a JSON evidence manifest for one capture analysis. It is not a
Unity asset and it is not a replacement for a runtime object dumper.

The HSR 4.5 profile combines three independently verifiable sources:

- RenderDoc `shader-export` output: EID, pipeline, stage, shader hash,
  interface hash, variant hash, reflection and resource snapshots.
- AnimeStudio shader/material reports: Unity shader names, PathIDs, material
  properties, texture references and generated ShaderLab source paths.
- An optional explicit link map. No name, hash or ordering heuristic is used
  to link AS assets to capture shaders.

The `renderDoc` and `as` sections contain relative paths into the output
directory. The `links` section is empty unless `--link-map` is supplied and
every endpoint validates against both input reports.

When `export` receives `--as-index`, the `as` section also records the copied
index path, schema, path-validation status, asset count, and exact
Material→Shader/Texture binding counts. The complete evidence remains under
`as/`; the top-level values are only a compact integrity summary.

客体运行时事件清单还包含 `coverage` 和 `observation`：`startup-only` 表示在观察
窗口内只看到启动阶段对象，`scene-observed` 表示至少出现了网格、粒子、Shader、
动画或相机等场景类型。`guest-event-seconds` 是初始观察窗口；如果配置了更大的
`guest-event-max-seconds`，工具会在场景类型出现并满足 `guest-event-idle-seconds`
后提前收尾，未确认场景时最多等到上限。HSR Profile 还可以配置一次性的自动进入操作，
其实际窗口尺寸、点击坐标和执行结果会写入 `observation.autoEnterAction`。
`guest-total-timeout-seconds` 限制启动、进入和采集的总时长。事件清单还记录
`observation.stopReason`，用于区分场景稳定收尾和达到上限。该状态只描述采集覆盖范围，不代表已经完成 Unity
PathID、资源路径或字段解码。

## Incremental capture enrichment

An existing session can be enriched without rebuilding its runtime records:

```powershell
my-hook-tool enrich 'session.hook' `
  --renderdoc 'renderdoccmd.exe' `
  --event 436
```

`--event` is mandatory and may be repeated. The command invokes the modified
RenderDoc exporter only for the selected EIDs, stores the manifest and stage
artifacts under `renderdoc/events-<EID>/`, and updates the existing `renderDoc`
and `artifacts` sections. It never treats an omitted event list as permission
to scan the whole capture. Resource snapshots remain opt-in because they can
be large.

The resulting `renderDoc.mode` is `selected-events`, and `selectedEvents`
records the exact EIDs used. Shader hashes, interface hashes, pipeline
ResourceIds, variant identities, stages and reflection bindings are exact
capture-local evidence. They do not prove the original Unity path, material
name or keyword assignment; those still require a same-session AS/runtime
record or an explicit validated link map.

## Example

```powershell
my-hook-tool export `
  'mumu-host_sr_frame3630.srrdc' `
  --renderdoc 'renderdoccmd.exe' `
  --output '.\artifacts\frame3630' `
  --event 436 `
  --reconstruct `
  --spirv-cross 'spirv-cross.exe' `
  --export-resources `
  --as-report 'shader-report.json' `
  --as-bindings 'unity-material-bindings.json' `
  --as-index 'as-asset-index.json'
```

The command writes `frame3630.hook` and a `renderdoc/` directory. It copies
the supplied AS reports under `as/` so the package remains auditable.

## Runtime analysis

For a `.hook` containing `records`, run:

```powershell
my-hook-tool analyze '.\hsr-object-snapshot.hook'
```

The command writes `hsr-object-snapshot.analysis.json`. Its `typeCounts` and
`snapshotCounts` are direct counts from the event records. `snapshotReferences`
contains only aligned 8-byte values that exactly match an observed object,
Transfer, or vtable address. Unknown values are left uninterpreted. The
`objectSnapshotHex` field is a bounded native-memory sample and is not, by
 itself, a decoded Unity object or a resource path.

When a `TypeTreeRipper.ttbin` artifact is attached, `analyze` also emits its
exact RTTI and field-node data under `decodedTypeTree`. An external file can be
selected with `--typetree <file.ttbin>`. The standalone `typetree` command reports
per-type field coverage against an RTTI catalog or an explicit required-type list.
If no valid `.ttbin` is available, the analysis reports `not_available` or `invalid`
rather than inventing field names. An otherwise valid file with zero field nodes is
reported as `decoded_empty` and is not treated as a usable complete TypeTree.

`analyze.identityBridge` is a separate runtime gate. `not_observed` means the input
contains no `unity.draw-context`; `partial` means such records exist but do not yet
provide every required source-file/PathID and shared RenderDoc event field;
`verified` requires all of those fields. This status is diagnostic only and never
creates a cross-source link by itself.

The HSR profile may also attach `typetree.baseline` with status
`offline_baseline`. This is a local, version-checked field source copied from
the repository baseline; it is deliberately separate from the guest-generated
`artifacts` entry and must not be reported as live runtime output.
`analyze` exposes these independently as `decodedTypeTree` and
`decodedTypeTreeBaseline`.

When native generation was requested but produced no non-empty guest artifact,
the session is marked `completed_no_runtime_typetree`. If the same session also
contains a valid profile baseline, the status is
`completed_with_offline_typetree`: the selected 4.5 baseline is usable for
offline decoding, but the guest process did not provide live TypeTree evidence.

## Coordinated runtime session

For sessions started through RenderDoc TargetControl, the `session` object may
contain both of these fields:

- `targetControlIdent`: the RenderDoc TargetControl listening port returned by
  `renderdoccmd`; it identifies the control endpoint, not a process.
- `targetPid`: the actual target process PID resolved through that endpoint.

They are intentionally separate. A port value must never be used as a process
ID, and a `.hook` is only considered capture-linked when its
`captureSessionId` also matches the records and capture metadata being aligned.

When `mumu --auto-trigger` is used, `session.captureTemplate` stores the
RenderDoc filename template and `session.captureFile` stores the confirmed
file path relative to the `.hook`, or the template when the wait expires.
`session.captureStatus` is `captured` only after the file is non-empty and its
size is unchanged across two checks; otherwise it is `capture_pending`.
For a manual RenderDoc session, the initial state is `awaiting_manual_capture`.

For MuMu, the host launcher and the Vulkan drawing process can have different
TargetControl endpoints. The HSR profile selects `MuMuNxDevice` as
`renderDocTargetName`; capture requests must be sent to that target rather than
the `MuMuNxMain` launcher endpoint. The standalone `trigger` command accepts
the same target name with `--target` and can update an existing session with
`--hook` after receiving RenderDoc's `NewCapture` message.
When a capture is linked this way, `session.captureTarget` records the
selector used for the capture, such as `MuMuNxDevice` or `ident:38920`.
