"""Inventory serialized Shader containers without executing the game or guessing layouts."""

import argparse
import hashlib
import json
import struct
import time
import zipfile
from collections import Counter
from pathlib import Path

import UnityPy


def digest(data):
    return hashlib.sha256(data).hexdigest()


def inventory(apk, output, version):
    start = time.monotonic()
    output.mkdir(parents=True, exist_ok=True)
    report = {
        "schema": "my-hook.static-apk-shader-inventory.v1",
        "declaredPackageVersion": version,
        "apk": str(apk.resolve()),
        "parser": "UnityPy " + UnityPy.__version__,
        "scope": "APK assets/bin/Data only; not downloaded hot-resource coverage",
        "identityPolicy": "Serialized container and PathID only; no runtime or EID association",
        "files": [],
        "errors": [],
    }
    with apk.open("rb") as stream:
        report["apkSha256"] = hashlib.file_digest(stream, "sha256").hexdigest()
    counts = Counter()
    with zipfile.ZipFile(apk) as archive:
        for entry in archive.infolist():
            if not entry.filename.startswith("assets/bin/Data/") or entry.is_dir():
                continue
            if "/Managed/" in entry.filename or "/Resources/" in entry.filename:
                continue
            if entry.file_size > 64 * 1024 * 1024:
                report["errors"].append({"entry": entry.filename, "reason": "bounded_file_limit"})
                continue
            data = archive.read(entry)
            # Check the serialized header before giving unknown data to the parser.
            if len(data) < 20:
                continue
            metadata, size, fmt, offset = struct.unpack_from(">4I", data)
            if not (9 <= fmt <= 22 and 0 < metadata <= len(data) and 20 <= offset <= len(data)):
                continue
            if fmt < 22 and size != len(data):
                report["errors"].append({"entry": entry.filename, "reason": "split_or_truncated_serialized_file"})
                continue
            try:
                env = UnityPy.load(data)
                objects = env.objects
                for obj in objects:
                    counts[obj.type.name] += 1
                targets = [obj for obj in objects if obj.type.name in ("Shader", "Material")]
                if not targets:
                    continue
                record = {
                    "entry": entry.filename, "sha256": digest(data), "size": len(data),
                    "objects": [],
                }
                shaders = [obj for obj in targets if obj.type.name == "Shader"]
                if shaders:
                    path = output / "containers" / Path(entry.filename).name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(data)
                    record["localFile"] = str(path.resolve())
                for obj in targets:
                    kind = obj.type.name
                    raw = obj.get_raw_data()
                    item = {
                        "class": kind, "pathId": obj.path_id, "byteStart": obj.byte_start,
                        "byteSize": obj.byte_size, "sha256": digest(raw),
                        "embeddedTypeTree": bool(obj.serialized_type.node),
                        "typeHash": obj.serialized_type.old_type_hash.hex(),
                    }
                    item["fieldParseStatus"] = "not_attempted_requires_validated_layout"
                    record["objects"].append(item)
                report["files"].append(record)
                print(json.dumps({"entry": entry.filename, "shaders": len(shaders),
                                  "materials": len(targets) - len(shaders)}), flush=True)
            except Exception as error:
                report["errors"].append({"entry": entry.filename, "reason": str(error)})
    report["objectCounts"] = dict(counts)
    report["elapsedSeconds"] = round(time.monotonic() - start, 3)
    target = output / "inventory.json"
    target.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({"report": str(target), "objectCounts": dict(counts),
                      "shaderContainers": sum("localFile" in x for x in report["files"]),
                      "errors": len(report["errors"]), "elapsedSeconds": report["elapsedSeconds"]}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--package-version", required=True)
    args = parser.parse_args()
    inventory(args.apk, args.output, args.package_version)
