"""Bounded HSR APK program extraction; preserve unresolved record words as opaque evidence."""

import argparse
import hashlib
import json
import struct
from pathlib import Path

import lz4.block


def verify_metadata(path, containers):
    provenance_path = path.parent / "metadata-provenance.json"
    provenance = json.loads(provenance_path.read_text(encoding="utf-8"))
    if provenance["schema"] != "my-hook.static-as-metadata-provenance.v1" or provenance["status"] != "fresh_export_inputs_unchanged":
        raise ValueError("Missing successful AS metadata provenance")
    if hashlib.sha256(path.read_bytes()).hexdigest() != provenance["reportSha256"]:
        raise ValueError("Metadata report changed since the actual AS export")
    for name, expected in {**provenance["inputs"], **provenance.get("resources", {})}.items():
        if name != Path(name).name or hashlib.sha256((containers / name).read_bytes()).hexdigest() != expected:
            raise ValueError("Metadata source container differs from its provenance")
    return provenance


class Reader:
    def __init__(self, data, position, end):
        if not 0 <= position <= end <= len(data):
            raise ValueError("Serialized object range exceeds the actual container")
        self.data, self.position, self.end = data, position, end
        self.ranges = []

    def take(self, size):
        if size < 0 or self.position + size > self.end:
            raise ValueError(f"Object bounds exceeded at {self.position} for {size} bytes")
        value = self.data[self.position:self.position + size]
        self.position += size
        return value

    def u32(self):
        return struct.unpack("<I", self.take(4))[0]

    def align(self):
        padding = self.take((-self.position) % 4)
        if any(padding):
            raise ValueError("Nonzero alignment padding")

    def array(self, label, width=4):
        start = self.position
        count = self.u32()
        if count > 16 * 1024 * 1024 // width:
            raise ValueError(f"Bounded array limit: {label}={count}")
        data = self.take(count * width)
        self.ranges.append({"field": label, "offset": start, "count": count, "width": width})
        if width == 1:
            self.align()
            return data
        return list(struct.unpack("<" + ("I" if width == 4 else "Q") * count, data))


def parse_platforms(data, shader, opaque_word):
    meta = shader["compiledData"]
    reader = Reader(data, meta["platformsEnd"], meta["objectEnd"])
    count = reader.u32()
    if count != len(meta["platforms"]) or count > 8:
        raise ValueError("Platform count disagrees with parsed metadata")
    platforms = []
    for index in range(count):
        reader.ranges = []
        record = {"platform": meta["platforms"][index], "start": reader.position}
        for field, width in (("infoOffsets", 4), ("infoLengths", 4), ("infoBlob", 1),
                             ("dictionary", 1), ("codeOffsets", 4), ("compressedLengths", 4),
                             ("decompressedLengths", 4), ("compressionMask", 8), ("codeBlob", 1)):
            record[field] = reader.array(field, width)
        if opaque_word:
            record["unidentifiedWordOffset"] = reader.position
            record["unidentifiedWord"] = reader.u32()
        n = len(record["codeOffsets"])
        if not n or any(len(record[field]) != n for field in (
                "infoOffsets", "infoLengths", "compressedLengths", "decompressedLengths")):
            raise ValueError("Program table lengths disagree")
        for program in range(n):
            offset, size = record["infoOffsets"][program], record["infoLengths"][program]
            if offset + size > len(record["infoBlob"]):
                raise ValueError("Info program exceeds its blob")
            offset, size = record["codeOffsets"][program], record["compressedLengths"][program]
            if offset + size > len(record["codeBlob"]):
                raise ValueError("Code program exceeds its blob")
        record["ranges"] = reader.ranges
        record["end"] = reader.position
        platforms.append(record)
    # Dependencies and immutable texture bindings are not needed for this bounded
    # sample, but nonempty tables are refused rather than silently skipped.
    if reader.u32() != 0 or reader.u32() != 0:
        raise ValueError("Nonempty shader dependency/footer tables require another adapter")
    baked = reader.take(1)[0]
    if baked not in (0, 1):
        raise ValueError("Invalid baked flag")
    reader.align()
    if reader.position != reader.end:
        raise ValueError(f"Object consumption mismatch: {reader.position} != {reader.end}")
    return platforms


def decode(record, index):
    offset = record["codeOffsets"][index]
    size = record["compressedLengths"][index]
    expected = record["decompressedLengths"][index]
    if size == expected == 0:
        return b""
    if expected > 16 * 1024 * 1024:
        raise ValueError("Decoded program exceeds bounded limit")
    compressed = record["codeBlob"][offset:offset + size]
    use_dict = index // 64 < len(record["compressionMask"]) and bool(
        record["compressionMask"][index // 64] & (1 << (index % 64)))
    options = {"uncompressed_size": expected}
    if use_dict and record["dictionary"]:
        options["dict"] = record["dictionary"]
    value = lz4.block.decompress(compressed, **options)
    if len(value) != expected:
        raise ValueError("Decompressed length mismatch")
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--shader-report", type=Path, required=True)
    parser.add_argument("--containers", type=Path, required=True)
    parser.add_argument("--shader-name", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--opaque-record-word", action="store_true",
                        help="Explicit experimental layout; word semantics remain unknown")
    parser.add_argument("--max-programs", type=int, default=8)
    parser.add_argument("--program-indices", help="Extract only these exact program indices")
    args = parser.parse_args()
    provenance = verify_metadata(args.shader_report, args.containers)
    source = json.loads(args.shader_report.read_text(encoding="utf-8-sig"))
    selected = [x for x in source["shaders"] if x["name"] == args.shader_name]
    if len(selected) != 1:
        raise ValueError("Shader name does not select exactly one serialized object")
    shader = selected[0]
    container = args.containers / shader["source"]["fileName"]
    data = container.read_bytes()
    platforms = parse_platforms(data, shader, args.opaque_record_word)
    args.output.mkdir(parents=True, exist_ok=True)
    report = {"schema": "my-hook.static-program-extraction.v1", "shader": shader["name"],
              "sourceFile": shader["source"]["fileName"], "pathId": shader["pathId"],
              "containerSha256": hashlib.sha256(data).hexdigest(),
              "metadataReportSha256": provenance["reportSha256"],
              "typeTreeHash": shader["typeTreeHash"],
              "layoutStatus": "bounded_sample_validated_opaque_word_unresolved" if args.opaque_record_word
                              else "bounded_sample_validated",
              "platforms": [], "programs": [],
              "limitations": ["No recovered runtime identity or original source code claimed."]}
    for record in platforms:
        report["platforms"].append({key: value for key, value in record.items()
                                    if key not in ("infoBlob", "dictionary", "codeBlob")})
        indices = ([int(x) for x in args.program_indices.split(",")] if args.program_indices
                   else range(min(args.max_programs, len(record["codeOffsets"]))))
        for index in indices:
            if not 0 <= index < len(record["codeOffsets"]):
                raise ValueError("Selected program index is outside platform table")
            value = decode(record, index)
            if not value:
                continue
            target = args.output / f"{record['platform']}_{index}.program.bin"
            target.write_bytes(value)
            info_offset, info_size = record["infoOffsets"][index], record["infoLengths"][index]
            info = record["infoBlob"][info_offset:info_offset + info_size]
            info_target = target.with_suffix(".info.bin")
            info_target.write_bytes(info)
            report["programs"].append({"platform": record["platform"], "index": index,
                                      "size": len(value), "sha256": hashlib.sha256(value).hexdigest(),
                                      "file": target.name, "infoFile": info_target.name,
                                      "prefixHex": value[:64].hex(), "infoPrefixHex": info[:64].hex()})
    (args.output / "program-extraction.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"shader": shader["name"], "programs": len(report["programs"]),
                      "status": report["layoutStatus"], "report": str(args.output / "program-extraction.json")}))


if __name__ == "__main__":
    main()
