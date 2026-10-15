# HSR 4.5 Device ASB Metadata Audit, 2026-09-17

## Source and Scope

Read-only inventory from MuMu ADB device `127.0.0.1:16384`, package
`com.miHoYo.hkrpg` version `4.5.0`. Only index/version metadata was copied;
no BLOCK, audio, video, or other game resource payload was copied.

Local files:
`artifacts/analysis/HSR-4.5/mumu-device-asb-metadata-20260917/`.

## Confirmed

- The guest `Asb/Android` directory contains 3029 `.block` files and occupies
  approximately 15 GB. The full package `files` directory is approximately
  47 GB; these totals are device-side and were not downloaded.
- `M_ArchiveV.bytes` contains JSON-lines metadata declaring HSR asset patch
  `4.5.16445860` for `M_Start_AsbV`, `M_Start_BlockV`, `M_AsbV`, and
  `M_BlockV`. The declared `ContentHash` and `FileSize` match the corresponding
  files copied to the local audit directory.
- Copied metadata includes `AsbV` (79,887,568 bytes), `BlockV` (72,868 bytes),
  startup AsbV (1,183,084 bytes), startup BlockV (220 bytes), the four 66-byte
  `M_*` marker files, `M_ArchiveV.bytes` (1,956 bytes), and
  `InstallVersion.bin` (58 bytes).
- `InstallVersion.bin` contains the text
  `e0af4bd5967a11f1bbb6caf015d37304,4.5.16121062,4.4.15614839`. The installed
  Android package reports 4.5.0; the local note preserves both values without
  interpreting them as interchangeable version identifiers.
- All ten copied files have SHA-256 values matching fresh `sha256sum` results
  on the guest. For the four named AsbV/BlockV payloads, computed MD5 also
  matches the 32-hex suffix in the filename and the `ContentHash` in
  `M_ArchiveV.bytes`.

## Observed, Not Inferred

- This proves a byte-identical local copy of the listed metadata and its
  manifest-declared version/hash/size consistency. It does not decode the
  binary AsbV/BlockV record layout.
- No current HSR 4.5 full AssetMap, CAB map, sourceFile-to-PathID table, or
  dependency closure has been generated from these files.
- The metadata does not by itself prove that a runtime opaque key is a PathID,
  or that any record identifies the Material/Shader seen in a particular
  RenderDoc event.

## Next Useful Validation

Implement or locate a version-matched AsbV/BlockV parser, validate its record
counts and referenced file hashes against the 3029 guest BLOCK inventory, then
use one parsed entry and one existing 4.5 BLOCK to compare AS's serialized
`sourceFile#PathID` output. Do not assign semantics based on the SRAM/SRBM
magic or binary field coincidence. Only after that source index is trusted can
runtime serialized identity values be checked against it.

