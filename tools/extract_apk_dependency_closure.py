"""Copy a bounded serialized-file external closure; missing targets remain explicit."""

import argparse
import hashlib
import json
import re
import struct
import zipfile
from pathlib import Path

import UnityPy


def serialized_header(data):
    if len(data)<20:
        raise ValueError("Truncated serialized header")
    metadata,size,fmt,offset=struct.unpack_from(">4I",data)
    if fmt==22:
        if len(data)<48:
            raise ValueError("Truncated format22 extended header")
        metadata,size,offset=struct.unpack_from(">IQQ",data,20)
    minimum=48 if fmt==22 else 20
    if not (9<=fmt<=22 and size==len(data) and 0<metadata<=len(data) and minimum<=offset<=len(data)):
        raise ValueError("Serialized declaration does not match assembled file; split suffix may be missing")
    return fmt


def extract(apk, root, output, max_files=128, max_bytes=64*1024*1024):
    if output.exists() and any(output.iterdir()):
        raise ValueError("Output must be new or empty; do not mix extraction runs")
    output.mkdir(parents=True, exist_ok=True)
    inputs=output/"inputs"
    inputs.mkdir(exist_ok=True)
    records=[]
    missing=[]
    total=0
    with zipfile.ZipFile(apk) as archive:
        names={}
        for entry in archive.infolist():
            if entry.filename.startswith("assets/bin/Data/") and not entry.is_dir():
                names.setdefault(Path(entry.filename).name,[]).append(entry)
        pending=[(None,root)]
        seen=set()
        while pending:
            parent,name=pending.pop(0)
            filename=name.replace("\\","/").split("/")[-1]
            if filename in seen:
                continue
            seen.add(filename)
            matches=names.get(filename,[])
            parts=[]
            if not matches:
                numbered=[]
                for key,items in names.items():
                    match=re.fullmatch(re.escape(filename)+r"\.split(\d+)",key)
                    if match:
                        if len(items)!=1:
                            raise ValueError("Ambiguous duplicate split entry")
                        numbered.append((int(match[1]),items[0]))
                numbered.sort()
                if numbered and [x[0] for x in numbered]==list(range(len(numbered))):
                    parts=[x[1] for x in numbered]
                    matches=[parts[0]]
            if len(matches)!=1:
                missing.append({"from":parent,"reference":name,"reason":"missing_or_ambiguous_apk_entry","matches":len(matches)})
                continue
            entry=matches[0]
            source_size=sum(x.file_size for x in parts) if parts else entry.file_size
            if len(records)>=max_files or total+source_size>max_bytes:
                raise ValueError("APK dependency closure exceeds the explicit file/byte budget")
            data=b"".join(archive.read(x) for x in parts) if parts else archive.read(entry)
            fmt=serialized_header(data)
            refs=[]
            env=UnityPy.load(data)
            if not env.assets:
                raise ValueError("No serialized file was parsed; external table is not verified")
            for asset in env.assets:
                refs.extend(x.path for x in asset.externals)
            pending.extend((filename,x) for x in dict.fromkeys(refs))
            (inputs/filename).write_bytes(data)
            total+=len(data)
            records.append({"file":filename,"entry":entry.filename,"format":fmt,"bytes":len(data),"sha256":hashlib.sha256(data).hexdigest(),"externals":refs})
            if parts:
                records[-1]["splitEntries"]=[{"entry":x.filename,"bytes":x.file_size} for x in parts]
    with apk.open("rb") as stream:
        apk_hash=hashlib.file_digest(stream,"sha256").hexdigest()
    report={"schema":"my-hook.apk-external-closure.v1","apkSha256":apk_hash,"root":root,
            "scope":"serialized external file table only; not runtime script or stream-data closure",
            "files":records,"missing":missing,"sourceBytes":total,"status":"external_table_closed" if not missing else "missing_external_files"}
    (output/"closure.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
    print(json.dumps({"files":len(records),"sourceBytes":total,"missing":missing}))


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk",type=Path,required=True)
    parser.add_argument("--root",required=True)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args()
    extract(args.apk,args.root,args.output)
