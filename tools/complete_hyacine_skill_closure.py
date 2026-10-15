"""Complete this bounded skill fixture, including its independently located APK texture."""
import hashlib
import json
import shutil
import subprocess
import zipfile
from pathlib import Path
from hsr_static_catalog import verify_content_file
from hsr_static_container import load_container
from probe_guest_asset_blocks import rows

ROOT=Path(__file__).resolve().parents[1]
V=ROOT/'artifacts/StaticShaderSamples/HSR-4.6'
A=V/'hyacine-attack-source'


def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()


def main():
    closure=A/'effect-closure'
    report=json.loads((closure/'closure.json').read_text())
    table=V/'battle-effect-source/catalog/BlockV_cdf91f78dcbfe53d593c8c58bd76816e.bytes'
    current={x['md5']+'.block':x for x in rows(table)}
    inputs=closure/'inputs'
    records=report['records']
    known={r['file'] for r in records}|set(report['deferred'])
    missing=sorted({x for r in records for x in r['externals']}-known)
    locations={
        'CAB-797be2a11d32edb74bfd1d1a70b6c7d3':'ed5fb88bc6ccdee626ad0622a7aa73b5.block',
        'CAB-a336bd993214120b23b1101577525c5c':'ebdccad25c812ae312d1a542d04e20fb.block',
        'CAB-b0dbf358048f6017f208843039e47f3c':'1a83a46ccecaec88b18a8c8149ab2c33.block',
        'CAB-ce883ef0114d15a4cc92c661e0b7ba55':'ed5fb88bc6ccdee626ad0622a7aa73b5.block',
        'CAB-da270e8e4054ce7938ab3d01b1a75401':'80c9bbf763af7756ebc917d8b90f41f7.block',
        'CAB-ff8927b015f140106eed3364ef981829':'9da1b7003436d0d86302830ab4399ab2.block',
        'CAB-c910a5d34fd14e73bce25653a1341af0':'08f658731eb8673e13c0888260014c44.block',
        'CAB-c10afee0224ed28335482c2a2a43fa8b':'ee72b90d1353792c0321d0462c253a2d.block',
    }
    if set(missing)!=set(locations):raise ValueError('Unexpected closure gap; do not silently generalize fixture locations')
    apk=ROOT/'artifacts/Updates/HSR-4.6/StarRail_4.6.0.apk'
    decoded=A/'tail-resources';decoded.mkdir(exist_ok=True)
    provenance=[]
    for name in missing:
        block_name=locations[name]
        source=closure/'blocks'/block_name
        origin=dict(kind='current_BlockV',table=str(table),tableSha256=sha(table))
        if name=='CAB-c910a5d34fd14e73bce25653a1341af0':
            source=decoded/block_name
            entry='assets/Asb/Android/'+block_name
            with zipfile.ZipFile(apk) as archive:
                data=archive.read(entry)
            if hashlib.md5(data).hexdigest()!=Path(block_name).stem:raise ValueError('APK block digest mismatch')
            source.write_bytes(data)
            origin=dict(kind='current_APK',apk=str(apk),apkSha256=sha(apk),entry=entry)
        else:
            if not source.exists():
                source=V/'battle-effect-source/discovery-large/blocks'/block_name
            row=current[block_name];verify_content_file(source,row['md5'],row['size'])
            origin['tableRow']=row['row']
        output=decoded/name
        decoder=ROOT/'src/StaticBlockProbe/bin/Release/net8.0-windows/StaticBlockProbe.exe'
        run=subprocess.run([str(decoder),str(source),str(output),'--extract-all','--resource-streams','--select-containers',name],
                           capture_output=True,text=True,encoding='utf-8',timeout=60)
        (output/'decoder.log').write_text(run.stdout+run.stderr,encoding='utf-8')
        if run.returncode:raise ValueError('Selected CAB decode failed')
        path=output/'containers'/name
        env=load_container(path)
        refs=list(dict.fromkeys(Path(r.path.replace('\\','/')).name for asset in env.assets for r in asset.externals))
        for p in (output/'containers').iterdir():shutil.copyfile(p,inputs/p.name)
        record=dict(file=name,sourceBlock=block_name,sha256=sha(path),externals=refs,origin=origin)
        records.append(record);provenance.append(record)
    known={r['file'] for r in records}|set(report['deferred'])
    unresolved=sorted({ref for r in records for ref in r['externals']}-known)
    if unresolved:raise ValueError('New unresolved identities: '+','.join(unresolved))
    # All serialized object externals now resolve; the original Shader library is parsed separately.
    final=dict(records=records,deferred=report['deferred'],unresolvedExternals=unresolved,
               status='exact_serialized_external_closure_shader_library_deferred',
               apkProvenance=provenance,policy='Source hashes and external identities, not filename resemblance')
    (A/'effect-closure-complete.json').write_text(json.dumps(final,indent=2),encoding='utf-8')
    print(json.dumps(dict(containers=len(records),deferred=report['deferred'],unresolved=len(unresolved))))


if __name__=='__main__':main()
