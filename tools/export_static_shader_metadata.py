"""Run the existing AS metadata exporter and anchor its report to unchanged input bytes."""

import argparse
import hashlib
import json
import subprocess
from pathlib import Path


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--as-cli", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--shader-path-ids", help="Exact serialized Shader PathIDs, comma separated")
    args = parser.parse_args()
    files = sorted(x.resolve() for x in args.source.iterdir() if x.is_file()
                   and x.suffix not in ('.resS', '.resource'))
    resources = sorted(x.resolve() for x in args.source.iterdir() if x.is_file()
                       and x.suffix in ('.resS', '.resource'))
    before = {x.name: sha(x) for x in files}
    resource_hashes = {x.name: sha(x) for x in resources}
    args.output.mkdir(parents=True, exist_ok=True)
    command = [str(args.as_cli.resolve()), str(args.source.resolve()), str(args.output.resolve()),
               "--game", "SR", "--batch_load", "--shader_report", "--types", "Shader:Parse", "Material:Parse",
               "--logger_flags", "Info", "Warning", "Error"]
    report_path = args.output / "shader-report.json"
    if args.shader_path_ids:
        ids = [int(x) for x in args.shader_path_ids.split(",")]
        command.append("--shader_path_ids=" + ",".join(map(str, ids)))
    old_timestamp = report_path.stat().st_mtime_ns if report_path.exists() else None
    completed = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", timeout=60)
    (args.output / "metadata-export.log").write_text(completed.stdout + completed.stderr, encoding="utf-8")
    if completed.returncode or not report_path.exists() or report_path.stat().st_mtime_ns == old_timestamp:
        raise ValueError("AS did not produce a fresh metadata report; inspect metadata-export.log")
    if {x.name: sha(x) for x in files} != before:
        raise ValueError("Input container changed during export")
    if {x.name: sha(x) for x in resources} != resource_hashes:
        raise ValueError('External texture resource changed during export')
    report = json.loads(report_path.read_text(encoding="utf-8-sig"))
    if {x["fileName"] for x in report["sourceFiles"]} != set(before):
        raise ValueError("Report source-file set disagrees with invocation inputs")
    assemblies = [args.as_cli] + list(args.as_cli.parent.glob("AnimeStudio*.dll"))
    provenance = {"schema": "my-hook.static-as-metadata-provenance.v1", "command": command,
                  "reportSha256": sha(report_path), "inputs": before, "resources": resource_hashes,
                  "exporterBinaries": {x.name: sha(x) for x in assemblies},
                  "status": "fresh_export_inputs_unchanged"}
    (args.output / "metadata-provenance.json").write_text(json.dumps(provenance, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(report_path), "shaders": len(report["shaders"]),
                      "materials": len(report["materials"]), "status": provenance["status"]}))


if __name__ == "__main__":
    main()
