"""Bounded field trace for a candidate schema; never adjusts offsets or fills defaults."""

import argparse
import hashlib
import json
import math
import re
import struct
from pathlib import Path

import UnityPy
from hsr_static_container import load_container


PRIMITIVES={"bool":"B","char":"B","SInt8":"b","UInt8":"B","short":"h","SInt16":"h","UInt16":"H",
            "unsigned short":"H","int":"i","SInt32":"i","unsigned int":"I","UInt32":"I","float":"f",
            "SInt64":"q","long long":"q","UInt64":"Q","unsigned long long":"Q","double":"d"}


def nodes_from_dump(path,class_id):
    selected=False
    nodes=[]
    pattern=re.compile(r"^(.+?) // ByteSize\{([\da-fA-F]+)\}, Index\{([\da-fA-F]+)\}, Version\{([\da-fA-F]+)\}, IsArray\{([\da-fA-F]+)\}, MetaFlag\{([\da-fA-F]+)\}$")
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        match=re.match(r"^// classID\{(-?\d+)\}:",line)
        if match:
            if selected:
                break
            selected=int(match[1])==class_id
        if not selected:
            continue
        trimmed=line.lstrip(" \t")
        match=pattern.match(trimmed)
        if match:
            kind,name=match[1].rsplit(" ",1)
            leading=line[:len(line)-len(trimmed)]
            size=int(match[2],16)
            nodes.append({"type":kind,"name":name,"byteSize":size if size<2**31 else size-2**32,
                          "index":int(match[3],16),"version":int(match[4],16),"flags":int(match[5],16),
                          "metaFlags":int(match[6],16),"level":leading.count("\t")+leading.count(" ")//4})
    if not nodes:
        raise ValueError("Requested class is missing from dump")
    return nodes


def build_tree(nodes):
    roots=[]
    stack=[]
    for source in nodes:
        item=dict(source,children=[])
        level=item["level"]
        while stack and stack[-1]["level"]>=level:
            stack.pop()
        if stack:
            if stack[-1]["level"]+1!=level:
                raise ValueError("Invalid schema level jump")
            stack[-1]["children"].append(item)
        else:
            if level!=0:
                raise ValueError("Schema lacks root")
            roots.append(item)
        stack.append(item)
    if len(roots)!=1:
        raise ValueError("Expected one schema root")
    return roots[0]


class SchemaError(ValueError): pass
class BoundaryError(ValueError): pass
class PolicyError(ValueError): pass


class TraceReader:
    def __init__(self,data,max_array=4096,endian="<"):
        if endian not in ("<",">"):
            raise SchemaError("Unsupported byte order")
        self.data=data
        self.position=0
        self.trace=[]
        self.max_array=max_array
        self.active=None
        self.endian=endian

    def take(self,size):
        if size<0 or size>len(self.data)-self.position:
            raise BoundaryError(f"object bounds: {size} bytes requested at {self.position}, {len(self.data)-self.position} available")
        result=self.data[self.position:self.position+size]
        self.position+=size
        return result

    def align(self):
        self.take((-self.position)%4)

    def read(self,node,path=""):
        if len(self.trace)>20000:
            raise PolicyError("Trace field budget exceeded")
        name=node["name"]
        path=path+"."+name if path else name
        begin=self.position
        record={"path":path,"type":node["type"],"schemaIndex":node["index"],"offset":begin,"version":node["version"]}
        self.trace.append(record)
        self.active=record
        kind=node["type"]
        children=node["children"]
        value=None
        try:
            if kind in PRIMITIVES:
                fmt=PRIMITIVES[kind]
                width=struct.calcsize(fmt)
                if node["byteSize"]!=width or children or node.get("flags",0)&1:
                    raise SchemaError("Primitive width/children/array flag conflict")
                value=struct.unpack(self.endian+fmt,self.take(width))[0]
                if kind=="bool" and value not in (0,1):
                    raise ValueError("invalid bool byte: "+str(value))
                if isinstance(value,float) and not math.isfinite(value):
                    if math.isnan(value) or name not in ("inSlope","outSlope","m_InSlope","m_OutSlope"):
                        raise PolicyError("nonfinite scalar requires an explicit semantic adapter")
                    record["value"]="Infinity" if value>0 else "-Infinity"
                else:
                    record["value"]=value
            elif kind=="string":
                length=struct.unpack(self.endian+"i",self.take(4))[0]
                if not 0<=length<=min(65536,len(self.data)-self.position):
                    raise ValueError("invalid string byte count: "+str(length))
                value=self.take(length).decode("utf-8",errors="strict")
                record["value"]=value
                self.align()
            elif kind=="TypelessData":
                length=struct.unpack(self.endian+"i",self.take(4))[0]
                self.take(length)
                record["byteCount"]=length
            elif kind=="Array" or (node.get("flags",0)&1):
                if len(children)!=2 or children[0]["name"]!="size":
                    raise SchemaError("Unsupported array schema")
                count_node=children[0]
                if count_node["type"] not in ("int","SInt32") or count_node["byteSize"]!=4 or count_node["children"] or count_node.get("flags",0)&1 or children[1]["name"]!="data":
                    raise SchemaError("Invalid array count/element schema")
                count=self.read(children[0],path)
                if not isinstance(count,int) or not 0<=count<=self.max_array:
                    raise PolicyError("bounded array count: "+str(count))
                record["count"]=count
                for i in range(count):
                    self.read(children[1],path+f"[{i}]")
            elif children:
                for child in children:
                    self.read(child,path)
            else:
                raise SchemaError("Unsupported leaf type: "+kind)
            if node["metaFlags"]&0x4000:
                self.align()
            record["end"]=self.position
            record["status"]="candidate_read"
            return value
        except (ValueError,UnicodeError) as error:
            record["status"]="rejected"
            record["end"]=self.position
            record["error"]=str(error)
            record["errorKind"]=type(error).__name__
            raise


def trace_object(container,path_id,schema,class_id,output):
    env=load_container(container)
    matches=[x for x in env.objects if x.path_id==path_id and x.type.value==class_id]
    if len(matches)!=1:
        raise ValueError("Target must be exactly one object with matching class")
    obj=matches[0]
    raw=obj.get_raw_data()
    if schema.suffix==".json":
        source=json.loads(schema.read_text(encoding="utf-8-sig"))
        candidates=[x for x in source["decodedTrees"] if x["rtti"]["persistentTypeId"]==class_id]
        if len(candidates)!=1:
            raise ValueError("Native schema class must be unique")
        nodes=candidates[0]["nodes"]
        flags=candidates[0].get("transferFlags")
    else:
        nodes=nodes_from_dump(schema,class_id)
        flags=None
    reader=TraceReader(raw,endian=obj.reader.endian)
    error=None
    try:
        reader.read(build_tree(nodes))
        if reader.position!=len(raw):
            raise ValueError(f"unconsumed object bytes: {len(raw)-reader.position}")
    except ValueError as failure:
        error=str(failure)
    rejected=[x for x in reader.trace if x.get("status")=="rejected"]
    first=max(rejected,key=lambda x:x["path"].count(".")) if rejected else None
    report={"schema":"my-hook.bounded-schema-trace.v1","sourceFile":container.name,"pathId":path_id,"classId":class_id,
            "containerSha256":hashlib.sha256(container.read_bytes()).hexdigest(),"objectSha256":hashlib.sha256(raw).hexdigest(),
            "serializedTypeHash":obj.serialized_type.old_type_hash.hex(),"objectBytes":len(raw),"cursor":reader.position,
            "candidateSchema":str(schema.resolve()),"candidateSchemaSha256":hashlib.sha256(schema.read_bytes()).hexdigest(),
            "transferFlags":flags,"firstRejectedField":first,"error":error,"fields":reader.trace,
            "byteOrder":reader.endian,
            "status":"candidate_schema_rejected" if error else "candidate_consumes_object_not_semantics_verified"}
    output.parent.mkdir(parents=True,exist_ok=True)
    output.write_text(json.dumps(report,indent=2),encoding="utf-8")
    print(json.dumps({"status":report["status"],"objectBytes":len(raw),"cursor":reader.position,"firstRejectedField":first,"error":error}))


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--container",type=Path,required=True)
    parser.add_argument("--path-id",type=int,required=True)
    parser.add_argument("--class-id",type=int,required=True)
    parser.add_argument("--schema",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args()
    trace_object(args.container,args.path_id,args.schema,args.class_id,args.output)
