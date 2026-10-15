"""Resolve exact current CAB externals via the verified guest directory, without name matching."""
import argparse
import hashlib
import json
import shutil
import subprocess
from pathlib import Path
from hsr_static_container import load_container
from probe_guest_asset_blocks import rows


def digest(p,algorithm='sha256'):
    with p.open('rb') as stream: return hashlib.file_digest(stream,algorithm).hexdigest()


def resolve(a):
    a.output.mkdir(parents=True,exist_ok=True)
    inputs=a.output/'inputs';inputs.mkdir(exist_ok=True)
    directory=json.loads((a.cache/'directory-index/cab-directory.json').read_text())
    by_name={}
    for r in directory['records']: by_name.setdefault(r['path'],[]).append(r)
    table=a.cache/'catalog/BlockV_cdf91f78dcbfe53d593c8c58bd76816e.bytes'
    current={r['md5']+'.block':r for r in rows(table)}
    local_blocks={p.name:p for p in a.cache.rglob('*.block')}
    # Only previously decoded containers with recorded source hashes are reused.
    local={}
    for inv in a.cache.rglob('inventory.ndjson'):
        for line in inv.read_text(encoding='utf-8-sig').splitlines():
            record=json.loads(line)
            block=Path(record['sourceBlock'])
            if block.name not in current: continue
            for f in record['files']:
                p=inv.parent/'containers'/f['sourceFile']
                if p.exists() and f.get('containerHash'): local.setdefault(f['sourceFile'],(p,f['containerHash'],block.name))
    pending=list(a.roots);seen=set();records=[];deferred=[]
    if a.resume:
        prior=json.loads((a.output/'closure.json').read_text())
        records=prior['records'];deferred=prior['deferred']
        for record in records:
            path=inputs/record['file'];row=current[record['sourceBlock']]
            if digest(path)!=record['sha256'] or row['row']!=record['tableRow']:
                raise ValueError('Resume input differs from recorded/current identity')
            source=local_blocks.get(record['sourceBlock'],a.output/'blocks'/record['sourceBlock'])
            if source.stat().st_size!=row['size'] or digest(source,'md5')!=row['md5']:
                raise ValueError('Resume source block differs from current catalog')
        seen.update(r['file'] for r in records);seen.update(deferred)
        pending=list(dict.fromkeys(prior['pending']+pending+
            [ref for r in records for ref in r['externals'] if ref not in seen]))
    def ensure(name):
        target=inputs/name
        if name in local:
            source,expected,block_name=local[name]
            if digest(source)!=expected: raise ValueError('Local container differs from its inventory: '+name)
            block=local_blocks[block_name]
            row=current[block_name]
            if block.stat().st_size!=row['size'] or digest(block,'md5')!=row['md5']:
                raise ValueError('Local source block differs from current BlockV')
            shutil.copyfile(source,target)
            return dict(sourceBlock=block_name,tableRow=row['row'],reused=True,sha256=expected)
        matches=by_name.get(name,[])
        if len(matches)!=1: raise ValueError(f'Directory identity must be unique: {name}, {len(matches)}')
        block_name=matches[0]['block'];row=current[block_name]
        block=local_blocks.get(block_name,a.output/'blocks'/block_name)
        if not block.exists():
            block.parent.mkdir(exist_ok=True)
            subprocess.run([str(a.adb),'-P','5037','-s',a.serial,'pull',a.guest_root+'/'+block_name,str(block)],
                           check=True,capture_output=True,timeout=60)
        if block.stat().st_size!=row['size'] or digest(block,'md5')!=row['md5']:
            raise ValueError('Downloaded block differs from current table')
        out=a.output/'probes'/name
        command=[str(a.decoder),str(block),str(out),'--extract-all','--resource-streams','--select-containers',name]
        run=subprocess.run(command,capture_output=True,text=True,encoding='utf-8',timeout=60)
        out.mkdir(parents=True,exist_ok=True)
        (out/'decoder.log').write_text(run.stdout+run.stderr,encoding='utf-8')
        if run.returncode: raise ValueError('Targeted container decode failed: '+name)
        source=out/'containers'/name
        if not source.exists(): raise ValueError('Directory target was not decoded')
        shutil.copyfile(source,target)
        resource=source.with_name(name+'.resS')
        if resource.exists(): shutil.copyfile(resource,inputs/resource.name)
        return dict(sourceBlock=block_name,tableRow=row['row'],reused=False,sha256=digest(source))
    while pending:
        name=pending.pop(0)
        if name in seen: continue
        seen.add(name)
        if name in a.defer:
            deferred.append(name);continue
        if name!=Path(name).name or not name.startswith('CAB-'): raise ValueError('Unexpected external file: '+name)
        if len(seen)>a.max_cabs: raise ValueError('Explicit CAB closure budget exceeded; resume with a reviewed --max-cabs budget')
        proof=ensure(name)
        env=load_container(inputs/name)
        refs=list(dict.fromkeys(Path(x.path.replace('\\','/')).name for asset in env.assets for x in asset.externals))
        for asset in env.assets:
            for obj in asset.objects.values():
                if obj.type.value!=28: continue
                tex=obj.read_typetree(check_read=True)
                stream=tex.get('m_StreamData',{})
                if not stream.get('size'): continue
                resource=Path(stream['path']).name
                if resource!=name+'.resS': raise ValueError('Unexpected texture stream reference')
                target=inputs/resource
                if not target.exists():
                    matches=list(a.cache.rglob(resource))
                    if matches:
                        values={digest(p) for p in matches}
                        if len(values)!=1: raise ValueError('Conflicting texture resource')
                        shutil.copyfile(matches[0],target)
                    else:
                        # Re-decode only this CAB and sibling resource, never the whole block output.
                        local.pop(name,None)
                        ensure(name)
                if not target.exists() or target.stat().st_size<stream['offset']+stream['size']:
                    raise ValueError('Missing or truncated texture stream')
        records.append(dict(file=name,**proof,externals=refs))
        if not a.shallow: pending.extend(refs)
        (a.output/'closure.json').write_text(json.dumps(dict(records=records,deferred=deferred,pending=pending,
            status='root_headers_only_not_dependency_closed' if a.shallow else 'selected_external_closure',
            unresolvedExternals=sorted(set(r for record in records for r in record['externals'])-set(r['file'] for r in records)-set(deferred)),
            policy='Exact external table identities; current BlockV digest and inventory hashes; shallow mode does not resolve dependencies, deferred Shader parsed separately.'),indent=2),encoding='utf-8')
        print(json.dumps(dict(done=len(records),pending=len(pending),file=name)),flush=True)
    print(json.dumps(dict(status='root_headers_only_not_dependency_closed' if a.shallow else 'selected_external_closure',files=len(records),deferred=deferred)))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for n in ['cache','output','adb','decoder']:p.add_argument('--'+n,type=Path,required=True)
    p.add_argument('--serial',required=True)
    p.add_argument('--guest-root',required=True)
    p.add_argument('--roots',nargs='+',required=True)
    p.add_argument('--defer',nargs='*',default=[])
    p.add_argument('--shallow',action='store_true',help='Read root CABs only; externals remain unresolved')
    p.add_argument('--resume',action='store_true',help='Verify and resume an existing closure; never skip source hash checks')
    p.add_argument('--max-cabs',type=int,default=128)
    args=p.parse_args()
    if not 1<=args.max_cabs<=512:p.error('--max-cabs must be 1..512')
    if args.shallow and args.resume:p.error('Resume is only supported for dependency traversal')
    resolve(args)
