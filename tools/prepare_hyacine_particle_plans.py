"""Bind verified Unity materials/meshes to all five byte-compared source particle subtrees."""
import hashlib
import json
import zipfile
import struct
from pathlib import Path
from prepare_static_particle_sample import prepare
from hsr_static_container import load_container

ROOT=Path(__file__).resolve().parents[1]
A=ROOT/'artifacts/StaticShaderSamples/HSR-4.6/hyacine-attack-source'
PROJECT=Path('D:/unity-extraction-validation')
OUTPUT='Assets/RenderDocShaderSamples/HSR-4.6/HyacineSkill01'


def main():
    meshes=json.loads((A/'native-effect-mesh-bindings.json').read_text())
    for mesh in meshes:
        if hashlib.sha256((PROJECT/mesh['asset']).read_bytes()).hexdigest()!=mesh['assetSha256']:raise ValueError('Mesh asset changed')
    preview=A/'effect-empty-scene-fixture.json'
    preview.write_text(json.dumps(dict(displayMode='native',cameraSize=2,cameraZ=-5,cameraFar=100,
        times=[0.1,0.3,0.5,0.8,1.2],sampleTime=0.3,cycleDuration=2,visibleTimes=[],emptyTimes=[],cameraCenteredOnSourceRoot=True,
        limitation='Explicit empty scene, native source units. No recovered battle camera, depth or volumetric fog.')),encoding='utf-8')
    plans=[]
    for package in sorted((A/'effect-export').rglob('*.srprefab')):
        name=package.stem;comparison=A/'effect-comparison'/name/'comparison.json'
        target=A/'particle-plans'/(name+'.json')
        prepare(package,comparison,PROJECT,PROJECT/OUTPUT/'Materials',target,preview)
        plan=json.loads(target.read_text());plan['nativeMeshes']=meshes
        with zipfile.ZipFile(package) as archive:
            manifest=json.loads(archive.read('manifest.json'))
            mono=next(c for c in manifest['Nodes'][0]['Components'] if c['Type']=='MonoBehaviour' and c['MonoBehaviour']['ClassName']=='MonoEffect')
            values=json.loads(archive.read(mono['ParametersFile']))
            source=A/'effect-closure/inputs'/mono['SourceCAB']
            obj=next(o for o in load_container(source).objects if o.path_id==mono['PathID'] and o.type.value==114)
            raw=obj.get_raw_data()
            if obj.serialized_type.old_type_hash.hex()!='a39960593b002eaebeb69ecf1380deb9':raise ValueError('Unsupported MonoEffect layout')
            for key,offset in [('Delay',40),('SelfTimeSlow',68),('MaxLifeTime',84)]:
                if struct.pack('<f',values[key])!=raw[offset:offset+4]:raise ValueError('Independent MonoEffect scalar mismatch')
            plan['sourceMonoEffect']=dict(sourceFile=mono['SourceCAB'],pathId=mono['PathID'],parameters=values,
                objectSha256=hashlib.sha256(raw).hexdigest(),scope='Selected typed fields; not recovered MonoEffect execution code')
            source_empty=all(c['data']['InitialModule']['maxNumParticles']==0 for n in plan['nodes'] for c in n['components'] if c['type']=='ParticleSystem')
            plan['emptyTimes']=plan['times'] if source_empty else []
            plan['visibleTimes']=[] if source_empty else [0.5]
        target.write_text(json.dumps(plan,indent=2),encoding='utf-8')
        plans.append(dict(plan=str(target),output=OUTPUT+'/Effects/'+name,report=str(A/'particle-validation'/name/'report.json')))
    jobs=A/'particle-jobs.json';jobs.write_text(json.dumps(plans,indent=2),encoding='utf-8')
    print(json.dumps(dict(jobs=str(jobs),effects=len(plans))))


if __name__=='__main__':main()
