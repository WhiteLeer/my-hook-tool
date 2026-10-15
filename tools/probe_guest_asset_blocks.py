"""Read bounded, content-verified guest BLOCK samples and inventory them with the existing AS decoder."""
import argparse
import hashlib
import json
import struct
import subprocess
from pathlib import Path


def rows(table):
    data=table.read_bytes()
    if data[:4]!=b'SRBM': raise ValueError('Not a BlockV table')
    offset,count,stride=struct.unpack_from('<3I',data,16)
    if stride!=24 or offset+count*stride!=len(data): raise ValueError('Unsupported bounded BlockV layout')
    result=[]
    for i in range(count):
        pos=offset+i*stride
        digest=b''.join(data[pos+j:pos+j+4][::-1] for j in range(0,16,4)).hex()
        opaque,size=struct.unpack_from('<2I',data,pos+16)
        result.append(dict(row=i,offset=pos,md5=digest,opaque=opaque,size=size))
    return result


def probe(args):
    args.output.mkdir(parents=True,exist_ok=True)
    available=rows(args.table)
    if args.names:
        selected=[r for r in available if r['md5'] in args.names]
        if len(selected)!=len(set(args.names)): raise ValueError('Selected content hashes absent from current table')
    else:
        # Discovery policy only: smaller files reduce cost; opaque words are never treated as asset types.
        selected=sorted((r for r in available if r['size']>=args.min_size),key=lambda r:r['size'])[:args.max_files]
    if len(selected)>args.max_files or sum(r['size'] for r in selected)>args.max_bytes: raise ValueError('Probe budget exceeded')
    records=[]
    inputs=args.output/'blocks'
    inputs.mkdir(exist_ok=True)
    for index,row in enumerate(selected):
        filename=row['md5']+'.block'
        destination=inputs/filename
        if not destination.exists():
            subprocess.run([str(args.adb),'-P',str(args.port),'-s',args.serial,'pull',args.guest_root.rstrip('/')+'/'+filename,str(destination)],check=True,capture_output=True,timeout=45)
        data=destination.read_bytes()
        if len(data)!=row['size'] or hashlib.md5(data).hexdigest()!=row['md5']: raise ValueError('Guest bytes do not match current table: '+filename)
        report=args.output/'probe'/row['md5']
        completed=subprocess.run([str(args.decoder),str(destination),str(report),'--extract-all'],capture_output=True,text=True,encoding='utf-8',timeout=45)
        (report/'decoder.log').write_text(completed.stdout+completed.stderr,encoding='utf-8')
        if completed.returncode: raise ValueError('AS bounded inventory failed: '+filename)
        native=json.loads((report/'inventory.ndjson').read_text(encoding='utf-8-sig'))
        names=[dict(sourceFile=f['sourceFile'],pathId=o['pathId'],name=o['name'],
                    sameContainerParticleCount=sum(x['classId']==198 for x in f['objects']))
               for f in native['files'] for o in f['objects'] if o.get('name')]
        import re
        candidates=[n for n in names if re.search(args.pattern,n['name'],re.I)
                    and (not args.require_particles or n['sameContainerParticleCount']>0)]
        particles=sum(o['classId']==198 for f in native['files'] for o in f['objects'])
        records.append(dict(**row,block=str(destination),inventory=str(report/'inventory.ndjson'),candidates=candidates,particleSystems=particles))
        print(json.dumps(dict(file=index+1,bytes=row['size'],containers=len(native['files']),particles=particles,candidates=candidates[:15])),flush=True)
        (args.output/'discovery.json').write_text(json.dumps(dict(schema='my-hook.guest-block-discovery.v1',
            tableSha256=hashlib.sha256(args.table.read_bytes()).hexdigest(),
            scope='Name-based discovery only; candidate ownership/dependency/behavior requires exact serialized PPtr validation',records=records),indent=2),encoding='utf-8')
        if candidates and args.stop_on_match: break


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for name in ('table','adb','decoder','output'): p.add_argument('--'+name,type=Path,required=True)
    p.add_argument('--guest-root',required=True)
    p.add_argument('--port',type=int,default=5037)
    p.add_argument('--serial',required=True)
    p.add_argument('--names',nargs='*')
    p.add_argument('--max-files',type=int,default=8)
    p.add_argument('--max-bytes',type=int,default=64*1024*1024)
    p.add_argument('--min-size',type=int,default=1000000)
    p.add_argument('--pattern',default=r'^Eff_Avatar_.*(?:Attack|Hit|Skill)')
    p.add_argument('--stop-on-match',action='store_true')
    p.add_argument('--require-particles',action='store_true')
    probe(p.parse_args())
