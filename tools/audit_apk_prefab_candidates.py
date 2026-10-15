"""Find bounded particle-prefab candidates in an APK with exact serialized references."""

import argparse
import hashlib
import json
import struct
import zipfile
from pathlib import Path

import UnityPy


def inventory(apk, output, target="ParticleSystem"):
    output.mkdir(parents=True, exist_ok=True)
    records = []
    containers = {}
    errors = []
    with zipfile.ZipFile(apk) as archive:
        for entry in archive.infolist():
            if not entry.filename.startswith("assets/bin/Data/") or entry.is_dir() or "/Managed/" in entry.filename or "/Resources/" in entry.filename:
                continue
            if entry.file_size < 20 or entry.file_size > 64 * 1024 * 1024:
                continue
            data = archive.read(entry)
            metadata, size, fmt, offset = struct.unpack_from(">4I", data)
            if not (9 <= fmt <= 22 and 0 < metadata <= len(data) and 20 <= offset <= len(data)) or (fmt < 22 and size != len(data)):
                continue
            try:
                env = UnityPy.load(data)
                targets = [obj for obj in env.objects if obj.type.name==target]
                if not targets:
                    continue
                filename = Path(entry.filename).name
                if filename in containers:
                    raise ValueError("Duplicate container basename")
                (output / "inputs").mkdir(exist_ok=True)
                (output / "inputs" / filename).write_bytes(data)
                containers[filename] = {"entry": entry.filename, "sha256": hashlib.sha256(data).hexdigest(), "size": len(data)}
                for obj in env.objects:
                    if obj.type.name not in ("GameObject", "Transform", "RectTransform", "ParticleSystem", "ParticleSystemRenderer", "Animation", "AnimationClip", "Animator", "MonoBehaviour", "MeshRenderer", "MeshFilter", "Mesh", "Camera"):
                        continue
                    record = {"sourceFile": filename, "pathId": obj.path_id, "class": obj.type.name,
                              "byteSize": obj.byte_size, "embeddedTypeTree": bool(obj.serialized_type.node),
                              "typeHash": obj.serialized_type.old_type_hash.hex()}
                    try:
                        if obj.type.name not in ("GameObject", "Transform", "RectTransform", "Animation"):
                            record["status"] = "not_decoded_requires_validated_schema"
                            record["externals"] = [x.path for x in obj.assets_file.externals]
                            records.append(record)
                            continue
                        record["fields"] = obj.read_typetree(check_read=True)
                        record["status"] = "strict_read_consumed_object_not_field_semantics_verified"
                    except Exception as error:
                        record["status"] = "field_read_failed"
                        record["error"] = str(error)
                    record["externals"] = [x.path for x in obj.assets_file.externals]
                    records.append(record)
                print(json.dumps({"entry": entry.filename, "target":target,"count":len(targets)}), flush=True)
            except Exception as error:
                errors.append({"entry": entry.filename, "error": str(error)})
    with apk.open("rb") as stream:
        apk_hash = hashlib.file_digest(stream, "sha256").hexdigest()
    report = {"schema": "my-hook.apk-particle-candidates.v1", "apkSha256": apk_hash,
              "scope": "APK serialized Data only, not complete game coverage", "target":target,"containers": containers,
              "objects": records, "errors": errors}
    (output / "inventory.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    particles = [x for x in records if x["class"]=="ParticleSystem"]
    print(json.dumps({"particleSystems": len(particles), "strictReads": sum("fields" in x for x in particles), "errors": errors}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--target", default="ParticleSystem", choices=["ParticleSystem","MeshRenderer"])
    args = parser.parse_args()
    inventory(args.apk, args.output,args.target)
