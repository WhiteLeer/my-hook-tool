"""Validate a read-only RTTI/code event against the exact local ELF, not an ABI."""
import argparse
import hashlib
import json
import struct
from pathlib import Path


def programs(library):
    if library[:6] != b"\x7fELF\x02\x01" or len(library) < 64:
        raise ValueError("ELF64 little-endian library required")
    if struct.unpack_from("<H", library, 18)[0] != 183:
        raise ValueError("AArch64 ELF required for this audit")
    phoff = struct.unpack_from("<Q", library, 32)[0]
    phsize, phcount = struct.unpack_from("<HH", library, 54)
    if phsize != 56 or not 0 < phcount <= 1024 or phoff + phsize * phcount > len(library):
        raise ValueError("Invalid ELF program headers")
    return [struct.unpack_from("<IIQQQQQQ", library, phoff + i * phsize) for i in range(phcount)]


def build_id(library):
    for kind, _, start, _, _, size, _, _ in programs(library):
        if kind != 4 or size > 1024 * 1024 or start + size > len(library):
            continue
        cursor, end = start, start + size
        while cursor + 12 <= end:
            name_size, desc_size, note_kind = struct.unpack_from("<III", library, cursor)
            cursor += 12
            name = library[cursor:cursor + name_size]
            cursor += (name_size + 3) & ~3
            if cursor + ((desc_size + 3) & ~3) > end:
                break
            desc = library[cursor:cursor + desc_size]
            cursor += (desc_size + 3) & ~3
            if note_kind == 3 and name == b"GNU\0" and desc:
                return desc.hex()
    raise ValueError("No bounded GNU Build ID note")


def executable_vaddr(library, offset, length):
    matches = []
    for kind, flags, start, vaddr, _, size, _, _ in programs(library):
        if kind == 1 and flags & 1 and offset >= start and length <= size - (offset - start):
            matches.append(vaddr + offset - start)
    if len(matches) != 1:
        raise ValueError("Code window is not uniquely inside an executable PT_LOAD")
    return matches[0]


def read_channel(event, prefix, size):
    vm_bytes, vm_error = event[prefix + "VmReadBytes"], event[prefix + "VmReadErrno"]
    mem_bytes, mem_error = event[prefix + "MemReadBytes"], event[prefix + "MemReadErrno"]
    if not all(isinstance(x, int) for x in (vm_bytes, vm_error, mem_bytes, mem_error)):
        raise ValueError("Invalid read channel counters")
    if vm_bytes < -1 or vm_bytes > size or mem_bytes < -1 or mem_bytes > size:
        raise ValueError("Invalid read channel byte count")
    if (vm_bytes == -1 and vm_error <= 0) or (vm_bytes >= 0 and vm_error != 0):
        raise ValueError("Contradictory process_vm_readv result")
    if vm_bytes == size and vm_error == 0:
        method = "process_vm_readv"
    elif mem_bytes == size and mem_error == 0:
        method = "proc_self_mem"
    else:
        raise ValueError("No complete successful read channel")
    return {"method": method, "vmBytes": vm_bytes, "vmErrno": vm_error, "memBytes": mem_bytes, "memErrno": mem_error}


def audit(event, library, profile, module, session_id, catalog):
    if event.get("kind") != "readonly-rtti-code-evidence":
        raise ValueError("Wrong event kind")
    contract = profile["readOnlyRttiLayout"]
    for key in ("structureBytes", "factoryPointerOffset", "typeIdOffset", "sizeOffset"):
        if event[key] != contract[key]:
            raise ValueError("Event disagrees with independent source layout contract")
    if not session_id or event["captureSessionId"] != session_id or module["captureSessionId"] != session_id:
        raise ValueError("Capture session mismatch")
    if event["processId"] <= 0 or event["threadId"] <= 0 or event["processId"] != module["processId"]:
        raise ValueError("Process provenance mismatch")
    if event["monotonicTimeNs"] < module["monotonicTimeNs"] or event["eventSequence"] <= module["eventSequence"]:
        raise ValueError("Module/event chronology mismatch")
    path = module["mappedPath"]
    if (not path.endswith("/libunity.so") or "/" + profile["androidPackage"] + "-" not in path
        or event["rttiMappingPath"] != path or event["codeMappingPath"] != path):
        raise ValueError("Module path provenance mismatch")
    expected_build = profile["targetLibraryBuildId"]
    if (build_id(library) != expected_build or module["backingFileBuildId"] != expected_build
        or event["backingFileBuildId"] != expected_build):
        raise ValueError("Build ID mismatch")
    if (module["loadedElfMachine"] != 183 or not module["headersEqual"]
        or bytes.fromhex(module["diskHeaderHex"]) != library[:64]
        or bytes.fromhex(module["loadedHeaderHex"]) != library[:64]):
        raise ValueError("Loaded ELF header mismatch")
    if event["runtimeArchitecture"] != module["observerArchitecture"]:
        raise ValueError("Observer architecture mismatch")
    raw = bytes.fromhex(event["rawHex"])
    if not event["rawReadable"] or len(raw) != event["structureBytes"] or not 0 < len(raw) <= 256:
        raise ValueError("Incomplete RTTI window")
    def field(name, fmt):
        offset = event[name]
        if not isinstance(offset, int) or offset < 0 or offset + struct.calcsize(fmt) > len(raw):
            raise ValueError("Invalid RTTI field offset")
        return struct.unpack_from(fmt, raw, offset)[0]
    if field("typeIdOffset", "<i") != event["selectedTypeId"]:
        raise ValueError("Raw type ID disagrees with catalog selection")
    if field("factoryPointerOffset", "<Q") != int(event["candidateFactoryAddress"], 16):
        raise ValueError("Raw factory pointer disagrees with candidate")
    size_value = field("sizeOffset", "<i")
    if catalog.get(event["selectedTypeId"]) != size_value:
        raise ValueError("Raw size disagrees with independent catalog row")
    rtti = int(event["rttiAddress"], 16)
    rtti_begin, rtti_end = int(event["rttiMappingBegin"], 16), int(event["rttiMappingEnd"], 16)
    if rtti < rtti_begin or len(raw) > rtti_end - rtti or not event["rttiMappingPermissions"].startswith("r"):
        raise ValueError("RTTI window outside readable mapping")
    raw_channel = read_channel(event, "raw", len(raw))
    loaded = bytes.fromhex(event["loadedCodeHex"])
    disk = bytes.fromhex(event["diskCodeHex"])
    length = event["codeBytes"]
    offset = event["candidateFileOffset"]
    if not isinstance(length, int) or not 0 < length <= 512 or not isinstance(offset, int) or offset < 0:
        raise ValueError("Invalid code window bounds")
    if len(loaded) != length or len(disk) != length or offset + length > len(library):
        raise ValueError("Incomplete/out-of-file code window")
    if not event["loadedCodeReadable"] or not event["diskCodeReadable"] or loaded != disk or loaded != library[offset:offset + length]:
        raise ValueError("Runtime, guest disk and local ELF bytes differ")
    address = int(event["candidateFactoryAddress"], 16)
    begin, end = int(event["codeMappingBegin"], 16), int(event["codeMappingEnd"], 16)
    if address < begin or length > end - address or offset != event["codeMappingFileOffset"] + address - begin:
        raise ValueError("Mapping arithmetic disagrees with code offset")
    vaddr = executable_vaddr(library, offset, length)
    code_channel = read_channel(event, "code", length)
    return {
        "status": "confirmed_bytes_only", "selectedTypeId": event["selectedTypeId"],
        "observedSizeUsingSourceLayout": size_value,
        "candidateFileOffset": hex(offset), "candidateElfVaddr": hex(vaddr),
        "candidateLoadBias": hex(address - vaddr), "codeBytes": length,
        "codeSha256": hashlib.sha256(loaded).hexdigest(),
        "rawReadChannel": raw_channel, "codeReadChannel": code_channel,
        "abiValidated": False, "factoryInvocationProven": False, "identityBridgeEstablished": False,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--events", type=Path, required=True)
    parser.add_argument("--library", type=Path, required=True)
    parser.add_argument("--expected-sha256", required=True)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--session-config", type=Path, required=True)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    library = args.library.read_bytes()
    sha = hashlib.sha256(library).hexdigest()
    if sha != args.expected_sha256.lower():
        raise SystemExit("Local library SHA-256 does not match the selected version")
    profile = json.loads(args.profile.read_text(encoding="utf-8-sig"))
    if sha != profile["targetLibrarySha256"]:
        raise SystemExit("Local library disagrees with version profile")
    config = dict(line.split("=", 1) for line in args.session_config.read_text(encoding="utf-8-sig").splitlines() if "=" in line)
    session_id = config["capture_session_id"]
    catalog = {int(row.split("|")[0]): int(row.split("|")[1]) for row in args.catalog.read_text(encoding="utf-8-sig").splitlines()[1:] if row}
    events = [json.loads(line) for line in args.events.read_text(encoding="utf-8-sig").splitlines()]
    modules = [event for event in events if event.get("kind") == "target-unity-module-metadata"]
    records = []
    for line_no, event in enumerate(events, 1):
        if event.get("kind") != "readonly-rtti-code-evidence":
            continue
        record = {"line": line_no, "eventSequence": event.get("eventSequence"), "processId": event.get("processId"), "threadId": event.get("threadId")}
        try:
            matching = [module for module in modules if module.get("processId") == event.get("processId") and module.get("captureSessionId") == session_id and module.get("mappedPath") == event.get("codeMappingPath")]
            if len(matching) != 1:
                raise ValueError("No unique same-process/session module snapshot")
            record.update(audit(event, library, profile, matching[0], session_id, catalog))
        except (ValueError, KeyError, TypeError, struct.error) as error:
            record.update(status="blocked", reason=str(error))
        records.append(record)
    report = {"format": "my-hook.readonly-rtti-audit.v1", "librarySha256": sha,
        "libraryPath": str(args.library.resolve()), "profilePath": str(args.profile.resolve()),
        "captureSessionId": session_id,
        "eventsSha256": hashlib.sha256(args.events.read_bytes()).hexdigest(),
        "scope": "Byte and ELF mapping verification only. No native call, ABI or asset identity proof.",
        "records": records}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0 if records and all(x["status"] == "confirmed_bytes_only" for x in records) else 2


if __name__ == "__main__":
    raise SystemExit(main())
