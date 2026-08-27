# `.hook` format

`.hook` is a JSON evidence manifest for one capture analysis. It is not a
Unity asset and it is not a replacement for a runtime object dumper.

The HSR 4.4 profile combines three independently verifiable sources:

- RenderDoc `shader-export` output: EID, pipeline, stage, shader hash,
  interface hash, variant hash, reflection and resource snapshots.
- AnimeStudio shader/material reports: Unity shader names, PathIDs, material
  properties, texture references and generated ShaderLab source paths.
- An optional explicit link map. No name, hash or ordering heuristic is used
  to link AS assets to capture shaders.

The `renderDoc` and `as` sections contain relative paths into the output
directory. The `links` section is empty unless `--link-map` is supplied and
every endpoint validates against both input reports.

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
  --as-report 'shader-report.json'
```

The command writes `frame3630.hook` and a `renderdoc/` directory. It copies
the supplied AS reports under `as/` so the package remains auditable.
