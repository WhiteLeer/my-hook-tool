"""Compare independent AS export values to bounded byte traces, with exact float bits."""
import argparse
import contextlib
import hashlib
import io
import json
import struct
import zipfile
from pathlib import Path

import UnityPy
from hsr_static_container import load_container
from trace_serialized_schema import trace_object, PRIMITIVES


def flatten(value,path="Base"):
    if isinstance(value,dict):
        for key,item in value.items():
            yield from flatten(item,path+"."+key)
    elif isinstance(value,list):
        yield path+".Array.size",len(value)
        for i,item in enumerate(value):
            yield from flatten(item,path+f".Array[{i}].data")
    else:
        yield path,value


def checked_flatten(value):
    result={}
    for path,item in flatten(value):
        if path in result:
            raise ValueError('Duplicate flattened path: '+path)
        result[path]=item
    return result


def validate_as_coverage(component,byte_count):
    return (component['ParametersStatus']=='external-type-tree-exported' and not component.get('ParametersError')
            and component['RemainingBytes']==0 and component['ParsedBytes']==component['ObjectByteSize']==byte_count)


def compare(package,containers,schema,output):
    output.mkdir(parents=True,exist_ok=True)
    results=[]
    seen=set()
    files={}
    node_states=[]
    def objects_in(name):
        if name not in files:
            files[name]=load_container(containers/name)
        return files[name].objects
    with zipfile.ZipFile(package) as archive:
        manifest=json.loads(archive.read("manifest.json"))
        for node in manifest["Nodes"]:
            candidates=[x for x in objects_in(manifest['SourceCAB']) if x.path_id==node['PathID'] and x.type.name=='GameObject']
            if len(candidates)!=1: raise ValueError('Node GameObject identity is not unique')
            obj=candidates[0]
            state=obj.read_typetree(check_read=True)
            if state['m_Name']!=node['Name']: raise ValueError('Source node name differs')
            node_states.append(dict(path=node['Path'],sourceFile=manifest['SourceCAB'],pathId=node['PathID'],
                active=state['m_IsActive'],layer=state['m_Layer'],tag=state['m_Tag'],
                objectSha256=hashlib.sha256(obj.get_raw_data()).hexdigest()))
            for component in node["Components"]:
                kind=component["Type"]
                if kind not in ("ParticleSystem","ParticleSystemRenderer"):
                    continue
                identity=(component['SourceCAB'],component['PathID'])
                if identity in seen:
                    raise ValueError('Duplicate component identity')
                seen.add(identity)
                trace_path=output/f"{component['SourceCAB']}_{component['PathID']}.json"
                with contextlib.redirect_stdout(io.StringIO()):
                    trace_object(containers/component["SourceCAB"],component["PathID"],schema,
                                 198 if kind=="ParticleSystem" else 199,trace_path)
                trace=json.loads(trace_path.read_text())
                if not validate_as_coverage(component,trace['objectBytes']):
                    raise ValueError('AS coverage/error metadata contradicts object bytes')
                exported=checked_flatten(json.loads(archive.read(component["ParametersFile"])))
                mismatch=[]
                compared=0
                nonempty_curves=[]
                pointers=[]
                for field in trace["fields"]:
                    path=field["path"]
                    if field.get("count",0)>0 and ".m_Curve.Array" in path:
                        nonempty_curves.append({"path":path,"count":field["count"],"offset":field["offset"]})
                    if field["type"].startswith("PPtr<"):
                        file_id=exported.get(path+'.m_FileID')
                        path_id=exported.get(path+'.m_PathID')
                        resolution={'status':'null'}
                        if path_id:
                            source_objects=list(objects_in(component['SourceCAB']))
                            owner=next(x for x in source_objects if x.path_id==component['PathID'])
                            if not isinstance(file_id,int) or not 0<=file_id<=len(owner.assets_file.externals):
                                raise ValueError('PPtr FileID is outside the source external-file table')
                            target_file=component['SourceCAB'] if file_id==0 else Path(owner.assets_file.externals[file_id-1].path).name
                            target_objects=[x for x in objects_in(target_file) if x.path_id==path_id]
                            if len(target_objects)!=1:
                                raise ValueError('Nonempty PPtr target is not unique')
                            expected_class=field['type'][5:-1]
                            target=target_objects[0]
                            if expected_class not in ('Object','Component') and target.type.name!=expected_class:
                                raise ValueError('PPtr target class mismatch')
                            resolution={'status':'target_class_verified','sourceFile':target_file,'pathId':path_id,
                                        'class':target.type.name,'objectSha256':hashlib.sha256(target.get_raw_data()).hexdigest()}
                        pointers.append({"path":path,"offset":field["offset"],
                                         "fileId":file_id,"pathId":path_id,"resolution":resolution})
                    if "value" not in field:
                        continue
                    actual=exported.get(path)
                    expected=field["value"]
                    try:
                        if field["type"] in PRIMITIVES:
                            fmt=trace["byteOrder"]+PRIMITIVES[field["type"]]
                            same=struct.pack(fmt,float(expected) if field["type"] in ("float","double") else expected)==struct.pack(fmt,actual)
                        else:
                            same=actual==expected
                    except (TypeError,ValueError,struct.error,OverflowError):
                        same=False
                    compared+=1
                    if not same:
                        mismatch.append({"path":path,"offset":field["offset"],"trace":expected,"as":actual})
                owning_go=exported.get("Base.m_GameObject.m_PathID")
                owner_valid=exported.get("Base.m_GameObject.m_FileID")==0 and owning_go==node["PathID"]
                go=[x for x in objects_in(component['SourceCAB']) if x.path_id==owning_go and x.type.name=='GameObject']
                if len(go)!=1:
                    raise ValueError('Owner must be an actual GameObject in this source container')
                go_data=go[0].read_typetree()
                reverse=[x['component'] for x in go_data['m_Component']]
                owner_valid=owner_valid and any(x['m_FileID']==0 and x['m_PathID']==component['PathID'] for x in reverse)
                compared_paths={f['path'] for f in trace['fields'] if 'value' in f}
                results.append({"sourceFile":component["SourceCAB"],"pathId":component["PathID"],"type":kind,
                                "node":node["Path"],"traceStatus":trace["status"],"objectSha256":trace["objectSha256"],
                                "containerSha256":trace["containerSha256"],"objectBytes":trace["objectBytes"],
                                "comparedValues":compared,"mismatches":mismatch,"ownerPPtrVerified":owner_valid,
                                "ownerReverseComponentVerified":owner_valid,
                                "nativeRootFields":sorted({f['path'].split('.')[1] for f in trace['fields'] if f['path'].startswith('Base.')}),
                                "asUncomparedFields":sorted(set(exported)-compared_paths),
                                "traceOpaqueFields":[f['path'] for f in trace['fields'] if 'byteCount' in f],
                                "nonemptyCurves":nonempty_curves,"pointers":pointers})
    passed=bool(results) and all(not x["mismatches"] and x["ownerPPtrVerified"] and
                x["traceStatus"]=="candidate_consumes_object_not_semantics_verified" for x in results)
    report={"schema":"my-hook.static-schema-comparison.v1","packageSha256":hashlib.sha256(package.read_bytes()).hexdigest(),
            "candidateSha256":hashlib.sha256(schema.read_bytes()).hexdigest(),"passed":passed,"components":results,"nodes":node_states,
            "scope":"AS and bounded trace agree on tested objects; does not prove custom field behavior or current runtime ABI"}
    (output/"comparison.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
    print(json.dumps({"passed":passed,"components":len(results),"values":sum(x["comparedValues"] for x in results),
                      "mismatches":sum(len(x["mismatches"]) for x in results)}))
    if not passed:
        raise ValueError("Independent parse comparison failed")


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package",type=Path,required=True)
    parser.add_argument("--containers",type=Path,required=True)
    parser.add_argument("--schema",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args()
    compare(args.package,args.containers,args.schema,args.output)
