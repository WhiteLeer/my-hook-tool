"""Convert recorded native nodes losslessly, retaining historical provenance and scope."""

import argparse
import hashlib
import json
from pathlib import Path

from trace_serialized_schema import nodes_from_dump


def write_candidate(source,other_dump,output):
    report=json.loads(source.read_text(encoding="utf-8-sig"))
    candidate=next(x for x in report["decodedTrees"] if x["rtti"]["persistentTypeId"]==198)
    nodes=candidate["nodes"]
    other=nodes_from_dump(other_dump,199)
    lines=["// Historical HSR 4.5 native ParticleSystem candidate; not a new 4.6 dump.","// Renderer is a separate historical candidate. Validate each current object."]
    for class_id,group in ((198,nodes),(199,other)):
        lines.append(f"// classID{{{class_id}}}: {group[0]['type']}")
        for node in group:
            fields=(node["byteSize"]&0xffffffff,node["index"]&0xffffffff,node["version"],node["flags"],node["metaFlags"])
            lines.append("\t"*node["level"]+node["type"]+" "+node["name"]+
                " // ByteSize{%x}, Index{%x}, Version{%x}, IsArray{%x}, MetaFlag{%x}"%fields)
        lines.append("")
    output.parent.mkdir(parents=True,exist_ok=True)
    output.write_text("\n".join(lines)+"\n",encoding="utf-8")
    keys=("type","name","level","byteSize","index","version","flags","metaFlags")
    roundtrip=nodes_from_dump(output,198)
    if len(roundtrip)!=len(nodes) or any(tuple(a[k] for k in keys)!=tuple(b[k] for k in keys) for a,b in zip(nodes,roundtrip)):
        raise ValueError("Native node conversion is not lossless")
    merged=nodes_from_dump(other_dump,198)
    compare=("type","name","level","byteSize","version","flags","metaFlags")
    canonical=lambda x:tuple(x[k] for k in compare)
    first=next((i for i,(a,b) in enumerate(zip(nodes,merged)) if canonical(a)!=canonical(b)),None)
    only_omission=first is not None and len(nodes)==len(merged)+1 and [canonical(x) for x in nodes[first+1:]]==[canonical(x) for x in merged[first:]]
    provenance={"schema":"my-hook.native-schema-candidate.v1","sourceNativeReport":str(source.resolve()),
                "sourceNativeSha256":hashlib.sha256(source.read_bytes()).hexdigest(),
                "otherSchemaSource":str(other_dump.resolve()),"otherSchemaSha256":hashlib.sha256(other_dump.read_bytes()).hexdigest(),
                "candidateSha256":hashlib.sha256(output.read_bytes()).hexdigest(),"nativeNodeRoundtripVerified":True,
                "sourceTransferFlags":candidate["transferFlags"],"sourceVersion":"historical HSR-4.5",
                "firstMergedDifferenceIndex":first,"mergedDiffIsOneOmittedNode":only_omission,
                "omittedNativeNode":nodes[first] if only_omission else None,
                "status":"candidate_requires_current_object_compatibility_and_semantics_verification"}
    output.with_suffix(".provenance.json").write_text(json.dumps(provenance,indent=2),encoding="utf-8")
    print(json.dumps({"nodes":len(nodes),"roundtrip":True,"oneOmittedNode":only_omission,"omitted":nodes[first]["name"] if only_omission else None}))


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-report",type=Path,required=True)
    parser.add_argument("--renderer-dump",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args()
    write_candidate(args.native_report,args.renderer_dump,args.output)
