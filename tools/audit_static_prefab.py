"""Reject incomplete effect packages before the legacy Unity importer can fill defaults."""

import argparse
import json
import zipfile
from pathlib import Path


def audit(package):
    with zipfile.ZipFile(package) as archive:
        manifest=json.loads(archive.read("manifest.json"))
        errors=[]
        components=[]
        complete_status={"external-type-tree-exported","type-tree-exported","renderer-type-tree"}
        for node in manifest["Nodes"]:
            for component in node["Components"]:
                kind=component["Type"]
                status=component.get("ParametersStatus","")
                record={"node":node["Path"],"class":kind,"sourceFile":component.get("SourceCAB"),
                        "pathId":component["PathID"],"status":status,
                        "objectBytes":component.get("ObjectByteSize",0),"parsedBytes":component.get("ParsedBytes",0),
                        "remainingBytes":component.get("RemainingBytes",0)}
                filename=component.get("ParametersFile")
                fields=json.loads(archive.read(filename)) if filename else {}
                count=fields.get("UnmappedTailBytes",0)
                raw=fields.get("UnmappedTailHex")
                if not isinstance(count,int) or count<0:
                    errors.append({"object":record,"reason":"invalid_opaque_tail_count"})
                    count=0
                try:
                    hex_count=len(bytes.fromhex(raw)) if isinstance(raw,str) else 0
                    if raw is not None and not isinstance(raw,str):
                        raise ValueError("Opaque tail must be hex text")
                except ValueError:
                    errors.append({"object":record,"reason":"invalid_opaque_tail_hex"})
                    hex_count=0
                if "UnmappedTailBytes" in fields and raw is not None and count!=hex_count:
                    errors.append({"object":record,"reason":"opaque_tail_fields_disagree"})
                record["rawTailBytes"]=max(count,hex_count)
                if "partial" in status or status.endswith(("unavailable","error")) or record["rawTailBytes"]:
                    errors.append({"object":record,"reason":"partial_or_unavailable_component"})
                if kind in ("ParticleSystem","ParticleSystemRenderer","Light"):
                    if not filename:
                        errors.append({"object":record,"reason":"missing_component_parameter_data"})
                    if status not in complete_status:
                        errors.append({"object":record,"reason":"component_not_structurally_complete"})
                    if record["objectBytes"]<=0 or record["parsedBytes"]!=record["objectBytes"] or record["remainingBytes"]!=0 or record["rawTailBytes"]:
                        errors.append({"object":record,"reason":"unparsed_or_inconsistent_object_coverage"})
                    if kind=="ParticleSystemRenderer":
                        renderer=component.get("ParticleRenderer") or {}
                        if not renderer.get("PrefixParsed") or renderer.get("UnparsedTailBytes",0):
                            errors.append({"object":record,"reason":"renderer_prefix_or_tail_unverified"})
                if component.get("ParametersError"):
                    errors.append({"object":record,"reason":"component_reports_parse_error","detail":component["ParametersError"]})
                components.append(record)
        if manifest.get("UnsupportedComponents"):
            errors.append({"reason":"unsupported_components","classes":manifest["UnsupportedComponents"]})
        if not manifest.get("Materials"):
            errors.append({"reason":"no_extracted_materials","detail":"Do not replace unresolved runtime bindings with a fallback material."})
        # Structural success alone is not a version/field-semantics contract.
        verified=False
        report={"schema":"my-hook.static-prefab-preflight.v1","package":str(Path(package).resolve()),
                "name":manifest["Name"],"sourceFile":manifest["SourceCAB"],"rootPathId":manifest["RootPathID"],
                "components":components,"errors":errors,"structuralGatePassed":not errors,
                "currentVersionSchemaVerified":verified,"unityImportAllowed":False,
                "status":"blocked_component_decode" if errors else "blocked_schema_semantics_verification"}
        return report


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package",type=Path,required=True)
    parser.add_argument("--report",type=Path,required=True)
    args=parser.parse_args()
    result=audit(args.package)
    args.report.parent.mkdir(parents=True,exist_ok=True)
    args.report.write_text(json.dumps(result,indent=2),encoding="utf-8")
    print(json.dumps({"status":result["status"],"errors":len(result["errors"]),"report":str(args.report)}))
    if not result["unityImportAllowed"]:
        raise SystemExit(2)
