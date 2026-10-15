# HSR 4.5 IL2CPP API-table static audit

## Scope

Exact HSR 4.5.0 Android ARM64 libraries copied from the installed MuMu package. This is a read-only static audit; no function semantics or API-table layout are inferred from another game/version.

## Confirmed

- `libil2cpp.so` SHA-256: `839dd86a6fe15336e907a57595d6f3841d460e9378c0f026d5f7ab1bbd834118`; ELF `.dynsym` contains 3388 entries. The relevant exported function is `il2cpp_get_api_table` at ELF value `0x631d898` (one-instruction branch stub). The other matching exports are `il2cpp_noelle_lock_world_mutex` and `il2cpp_noelle_unlock_world_mutex`. No Material, Shader, `MetadataCache_Register`, or `LoadMetadataFile` function appears as a matching dynamic symbol.
- Four `ELF_HOOK_*MetadataCache_Register/LoadMetadataFile` strings exist inside `.dynstr`, but none of their string offsets is referenced by a `.dynsym` `st_name` field. They are not usable exported-symbol names in this binary.
- `libunity.so` SHA-256: `3fd4436ae093c58a958ed78c144f198342b51ade083e7dae9f81d905fb2be68f`, matching the previously recorded installed-file hash. It contains the literal `il2cpp_get_api_table` at file/virtual address `0x3777e0` in `.rodata`.
- Both libraries have four additional `PT_LOAD` program headers whose file offsets alias a trailing data region not represented by ordinary named sections. The executable/read-only segment is 0x3550 bytes. In this mapped region, both libraries contain the same family of strings: `stub_dlsym`, `stub_decrypt_elf`, `g_tprt_pfn_array`, `g_tprt_ori_array`, and paired `ELF_HOOK_BRIDGE_*` / `ELF_HOOK_*` names for `MetadataCache_Register`, `LoadMetadataFile`, `InitializeRuntimeMetadata`, `il2cpp_init`, `il2cpp_runtime_invoke`, and other engine entry points. The same metadata-hook names also occur in the normal dynamic string tables.
- In `libunity.so`, the appended mapping begins at file offset `0x2bf0000`, virtual address `0x2e7c000`; the `ELF_HOOK_BRIDGE_MetadataCache_Register` string is at file offset `0x2bf0f50` / mapped address `0x2e7cf50`. In `libil2cpp.so`, the mapping begins at file offset `0x1a38c000`, virtual address `0x1aca0000`; the corresponding string is at file offset `0x1a38cf50` / mapped address `0x1aca0f50`.
- The exact R88 `events.ndjson` contains `native-bridge-load-ext-call` event 6 for the installed `libtprt.so` in the same HSR PID/capture session. The installed ARM64 library was selectively pulled to `artifacts/analysis/HSR-4.5/hsr45-runtime-support-libs-20260918/libtprt.so`; SHA-256 `41443b96c8fbdcd354a0c73c168fd3b98ab7a259e1915e2903a0a900dcff01a5`.
- `libtprt.so` dynamic symbols export generic `ELF_HOOK_BRIDGE_stub0..11` and `ELF_HOOK_STUB_stub0..11` functions, plus `g_tprt_pfn_array` (432-byte object) and `g_tprt_ori_array` (168-byte object). `libxhook.so` separately exports `xh_elf_hook`, `xhook_got_hook_symbol`, and `semi_dlsym`. This proves those generic hook components exist; it does not identify a specific target binding.
- In the exact `libtprt.so` file, all 12 `ELF_HOOK_BRIDGE_stub*` exported symbols have size `0x60` and their file-backed bytes are zero; all 12 neighboring `ELF_HOOK_STUB_stub*` exports have size `0xa0` and contain nonzero bytes. The latter begin with an indirect helper call followed by vector/integer register restoration; this pattern alone does not establish slot names or target ABI. The zero-filled bridge bodies make a runtime byte snapshot a discriminating test for loader-time materialization.
- Both `libil2cpp.so` and `libunity.so` list `libtprt.so` in `DT_NEEDED`. `libunity.so` imports `g_tprt_pfn_array` and `g_tprt_ori_array` with `R_AARCH64_GLOB_DAT` relocations at ELF virtual addresses `0x2e837c0` and `0x2e837d8` respectively. This is direct static evidence that libunity links against these array symbols.
- AArch64 disassembly of the appended executable mapping confirms indirect dispatch through the `g_tprt_pfn_array` GOT import: at VMA `0x2e7e5cc`, code loads the array address, then reads the slot at byte offset `0x38` and branches through it; subsequent dispatch stubs read offsets `0x40`, `0x48`, `0xb0`, `0xb8`, `0x188`, and others. This proves array-indexed dispatch exists, but not the semantic name or ABI of any slot.

## Observed, not proven

- A scan of the mapped `.text` for direct AArch64 `ADR` and contiguous `ADRP+ADD` references to `0x3777e0`, plus a scan of the dynamic relocation listing, found no direct reference. This does not rule out a reference through another register sequence, a generated table, or code outside `.text`.
- The appended `PT_LOAD` data appears to be a custom hook/loader-related region from its contents, but its record layout, producer, consumer, and runtime behavior have not been established. The `ELF_HOOK_*` strings are candidate identifiers only; they do not prove that those functions are hooked or invoked.
- The R88 event proves `libtprt.so` was loaded, not that its exported stub arrays contain a binding for any particular metadata function. The array sizes and generic stub names do not provide slot semantics.
- The current injected observer can read mapped memory through `ReadGuestValue`, but the R88 event stream does not contain a runtime snapshot of the guest ARM64 `libunity.so` GOT, `g_tprt_pfn_array`, or `g_tprt_ori_array`. A runtime base must be derived from the exact `/proc/self/maps` entry, mapping file offset, and matching installed library identity before any address is read; the static VMA must not be treated as a process address by itself.
- The `il2cpp_get_api_table` export entry branches to `0x63034d4`; the target body and its return/API-table ABI have not been decoded or validated. The export name and embedded Unity string alone do not prove what the function returns or how to call it.
- `global-metadata.dat` begins with `MHY\0` and is not directly parseable by the current static workflow. This observation does not prove whether a usable plaintext metadata copy exists in process memory.

## Blocked / next experiment

- No exact HSR 4.5 runtime observation yet shows the overlay being consumed, a `MetadataCache_Register` / `LoadMetadataFile` hook firing, a `g_tprt_*` array entry bound to a named target, or `libunity.so` calling `il2cpp_get_api_table` with its arguments/return and table entries. The raw snapshot does not establish that any bridge stub executes.
- Before using this export to enumerate IL2CPP methods or recover decrypted metadata, observe its actual invocation in a fresh HSR process under the exact 4.5.0 build. Preserve the raw entry registers, return register, caller PC, loaded module build IDs, and same-session capture ID. Do not call the function with guessed arguments or interpret table slots by index without independent ABI evidence.
- Runtime-verified in HSR session `hsr45-tprt-dispatch-snapshot-20260918-05`: the opt-in, read-only x86_64 host snapshot validates the exact Build IDs, derives load biases from observed readable mappings, reads the two GOT cells and bounded raw arrays, and copies same-build bridge bytes from readable mappings. It does not call/patch pointers or infer targets; the ARM64 guest copy does not start a duplicate thread. Toggle: `MY_HOOK_HSR45_TPRT_DISPATCH_SNAPSHOT=1`.
- Both Android targets compile, and the .NET 8 my-hook-tool project builds with zero warnings/errors. Current Android x86_64 / ARM64 SHA-256 values are `BD53F1D03C0E005CC7B051E99313E6E294EEEA95ADDFFD88FEFD08ABEBFE9C10` and `D80B5617DB05C80D515F26FCE76CD22ECAC386F08914B5360B962E4A8046DFB1`. The runtime event and array counts are recorded in the Runtime Snapshot Update section below.
- Next experiment: identify a real dispatch invocation with caller/target/register evidence, then trace it to a named target and ABI. Do not infer semantics from slot order or address proximity.
- Only after exact target binding and call ABI are independently established should any metadata hook or `il2cpp_get_api_table` be invoked/observed. This audit does not close TypeTree field decoding, `sourceFile#PathID`, Material/Shader binding, or RenderDoc EID identity.

## Reproduction notes

- `llvm-readelf --dyn-syms --wide libil2cpp.so` reports 3388 dynamic symbols; targeted text filtering finds `il2cpp_get_api_table` and the two Noelle mutex exports, but no Material/Shader/MetadataCache/LoadMetadataFile export.
- The `.dynsym` table starts at file offset `0x34d68`, has 24-byte entries; targeted `st_name`-offset scan confirms no entries refer to the four `ELF_HOOK_*` strings at `.dynstr` file offsets `0x348c5`, `0x348ec`, `0x349a8`, and `0x349c9`.
- `libunity.so` `.rodata` contains `il2cpp_get_api_table` at `0x3777e0`; current direct-reference scan is limited to AArch64 `ADR`, contiguous `ADRP+ADD`, and dynamic relocations.
- Program headers show the extra mappings; `llvm-strings -a -t x` over file-offset ranges `0x2bf0000..0x2bf3550` and `0x1a38c000..0x1a38f550` exposes the overlay labels and hook-name pairs. No overlay structure has yet been decoded.
- `libtprt.so` exports and R88 event 6 are independently checkable in the selectively pulled runtime-support library and R88 `events.ndjson`; neither proves a specific hook installation.
- `readelf -d` confirms `libtprt.so` in `DT_NEEDED` for both target libraries. `readelf -r` confirms the two `R_AARCH64_GLOB_DAT` slots in libunity. AArch64 disassembly confirms the slot-load/indirect-branch sequence at `0x2e7e5cc` and later stubs. The runtime snapshot event is recorded above.
- The exact libtprt `.dynsym` and file bytes verify the 12 zero-filled `ELF_HOOK_BRIDGE_stub*` exports (0x60 bytes each) and the 12 nonzero `ELF_HOOK_STUB_stub*` exports (0xa0 bytes each). In session `...-05`, all 12 runtime ranges exactly matched their same-build file bytes and remained all-zero.

## Runtime snapshot update (2026-09-18)

Session `hsr45-tprt-dispatch-snapshot-20260918-05` supersedes the earlier “not yet run” status above. The explicit snapshot toggle was enabled in the generated guest config and confirmed by `runtime-build` (`hsr45TprtDispatchSnapshotEnabled=true`). One event with status `observed_raw_values_only` was emitted and all 71 JSONL records parsed successfully; process 17544, eventSequence 1, captureSessionId `hsr45-tprt-dispatch-snapshot-20260918-05-98787a784e544850adb2d0801937f412`.

Observed in the same process/session:

- HSR 4.5.0 library Build IDs: libunity `5a4ba26d6687f32c`, libil2cpp `7e6d81db351101e1`, libtprt `e9e9ba3856e5c644983d3886abf9a4ddb253e77c`.
- The libunity overlay mapping at file offset `0x2bf0000` was readable (`r--p`), not executable. The previous execute-permission requirement was removed; bridge bytes are now copied only from readable, bounded same-module mappings.
- Runtime ranges for 54 `g_tprt_pfn_array` raw values and 21 `g_tprt_ori_array` raw values were validated against libtprt mappings; 12 bridge-stub byte ranges were copied.
- Direct equality comparison found no raw array entry exactly equal to any of the twelve captured bridge export addresses. This does not rule out indirect wrappers or another dispatch route.
- Event: `artifacts/HookSessions/hsr45-tprt-dispatch-snapshot-20260918-05/runtime/events.ndjson`. No slot names, target function identities, call semantics, or object identities are assigned.
- Session `...-04` caught a missing JSON quote in the event serializer. That defect was fixed before `...-05`; it is not counted as a successful validation.
- Current Android x86_64 / ARM64 artifact SHA-256 values are `BD53F1D03C0E005CC7B051E99313E6E294EEEA95ADDFFD88FEFD08ABEBFE9C10` / `D80B5617DB05C80D515F26FCE76CD22ECAC386F08914B5360B962E4A8046DFB1`. .NET 8 build passed with zero warnings and errors.

The same-build byte comparison is complete: all twelve runtime bridge ranges equal their file-backed zero bytes. The remaining gate is call-chain analysis. The snapshot is not proof that any stub executes or maps to a named function; TypeTree field decoding, sourceFile#PathID, Material→Shader binding, and RenderDoc EID identity remain unresolved.

## Runtime target-byte update (2026-09-18)

Session `hsr45-tprt-target-bytes-20260918-01` adds bounded event-time mappings for each non-null dispatch-array value. The `.hook` inspector reports 83 events (65 `runtime-build`, 16 `unity.vtable-probe`, one `native-bridge-interface`, one `hsr45-tprt-dispatch-snapshot`), a 4,684-byte runtime RTTI catalog, zero AS Shader/Material exports, and zero explicit associations.

- All 54 `pfnArrayRaw` entries map to the exact-build `libtprt.so` readable mapping. For every entry, the recorded event-time file offset and 16 raw bytes match the extracted same-build ELF bytes (54/54). `libtprt.so` SHA-256: `41443b96c8fbdcd354a0c73c168fd3b98ab7a259e1915e2903a0a900dcff01a5`.
- `oriArrayRaw` has 21 entries: 19 null and two non-null. Both non-null targets map to the `libunity.so` overlay (`r--p`, file offset `0x2bf0000`), and their recorded 16 bytes match the exact file bytes (2/2). `libunity.so` SHA-256: `3fd4436ae093c58a958ed78c144f198342b51ade083e7dae9f81d905fb2be68f`.
- The event records HSR PID `20912`, sequence `1`, session `hsr45-tprt-target-bytes-20260918-01-e04350bcc2034c4faf22260bdcfe46fc`, and exact library Build IDs. These are same-session pointer-to-mapping/file-byte observations, not function identity or invocation evidence.
- Current TypeTreeRipper build artifact SHA-256 values: Android x86_64 `5509525dd643e9259656297d96a9792135a2c8159fc0e1d762f75b116ae48be6`; ARM64 `9a47dea9152348dfe49c3ff7ce2dfd2c78ad1a536bc5f7d576a85167d37e1dd0`.

No event contains a caller PC, a target invocation, argument registers, return value, or named target binding. The 4,684-byte RTTI catalog is not field-level runtime TypeTree output; no `sourceFile#PathID`, Material-to-Shader edge, or RenderDoc EID bridge was produced. Do not infer slot meaning from offsets, RVAs, byte patterns, or code shape. The next gate is to prove a safe exact-build caller-to-target execution path and its ABI before adding any invocation observer.

## Reproducibility and bounded string-xref update (2026-09-18)

`Build-HSR45Runtime.ps1` was rerun after finding source/deployment artifact hashes differed. Both CMake builds were already up to date; the .NET 8 build passed with zero warnings/errors, and the script redeployed both Android libraries and refreshed `runtime/HSR-4.5-MUMU/manifest.json`. After deployment, source and packaged SHA-256 values match exactly:

- Android x86_64: `5509525dd643e9259656297d96a9792135a2c8159fc0e1d762f75b116ae48be6`.
- Android ARM64: `9a47dea9152348dfe49c3ff7ce2dfd2c78ad1a536bc5f7d576a85167d37e1dd0`.

These hashes describe the first synchronized build in this turn; they were superseded by the build-identity reporter build below.

This only repairs local build/package consistency. The active HSR process remained running; `/proc/20912/maps` showed the injected Ripper mapping as `/data/local/tmp/my-hook-typetree-ripper.so (deleted)`. A read-only pull through `/proc/20912/map_files/7b51ccc45000-7b51cccc7000` returned a 10,751,472-byte ELF whose SHA-256 is `5509525dd643e9259656297d96a9792135a2c8159fc0e1d762f75b116ae48be6` and GNU Build ID is `69d16fec21133a26c6fde77a80512cd980922460`. This is the same PID that emitted the session events and establishes the current live mapping's identity. Because the event itself lacks a module ID and no load/unload timeline was recorded, this is not proof that the mapping instance was continuous between the runtime-build and snapshot events. The app was not restarted while responsive.

A read-only Capstone/pyelftools scan checked all VMA aliases of six exact metadata-hook strings (`MetadataCache_Register`, `LoadMetadataFile`, `InitializeRuntimeMetadata`, with and without `ELF_HOOK_BRIDGE_`) against ADR/ADRP references from the exact appended executable PT_LOAD at file offset `0x2bf0000` / VMA `0x2e7c000` / size `0x3550`. No direct reference was found. This does not exclude dynamic lookup, indirect data tables, or code outside this bounded segment; it is not evidence that the strings are unused or that any target is bound.

The current next gate is to validate the emitted loaded-module Build ID from a fresh HSR process: x86_64 expected `57ac605d3855a6ce14feefe16b5848e61e01a781`, ARM64 expected `04879d4735b2decfbb5b58a6e666c8b4005d7bd8`. Then use that exact identity to scope any safe call observer. The observer must preserve the guest ABI while recording a real caller-to-target invocation. Existing selected-class runtime TypeTree artifacts (Light 187 nodes, Material 76 nodes, Shader 1551 nodes) remain verified in their earlier sessions; session `hsr45-tprt-target-bytes-20260918-01` itself emitted only the 4,684-byte RTTI catalog.

### Build identity reporter implementation

The next native change is now implemented. The delayed `runtime-module-identity` reporter waits until after dynamic-loader constructors, locates the Ripper image containing its own function pointer via `dl_iterate_phdr`, and reads only a bounded, readable GNU `PT_NOTE` range. It emits the module Build ID without calling guest functions, patching code, or serializing process addresses. Both Android ABIs and the .NET 8 project rebuilt successfully with zero managed warnings/errors; offline `Test-HSR45OpaqueCallsiteEvidence.ps1` still passes. Source and deployed artifact hashes match:

- Android x86_64 SHA-256 `244490594adca386d578392467d4eca36a21f4163c682715539a0a7517352c67`, GNU Build ID `57ac605d3855a6ce14feefe16b5848e61e01a781`.
- Android ARM64 SHA-256 `132517c8945e3bf24f0352b3dc08210844a6459aaf034086be0ba89a451decf2`, GNU Build ID `04879d4735b2decfbb5b58a6e666c8b4005d7bd8`.

These are build-artifact IDs only. The reporter has not yet been observed in HSR because the running game has not been restarted; its first fresh runtime record must match one of these IDs before that session's observations are attributed to the current build.
