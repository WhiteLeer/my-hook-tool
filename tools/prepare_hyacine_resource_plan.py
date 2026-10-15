"""Collect exact material samples and independently decoded particle mesh sources for Unity."""
import hashlib
import json
import zipfile
from pathlib import Path
from UnityPy.helpers.MeshHelper import MeshHandler
from hsr_static_container import load_container

ROOT=Path(__file__).resolve().parents[1]
A=ROOT/'artifacts/StaticShaderSamples/HSR-4.6/hyacine-attack-source'
OUTPUT='Assets/RenderDocShaderSamples/HSR-4.6/HyacineSkill01'


def sha(path):
    with path.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()


def main():
    results=json.loads((A/'effect-shader-results.json').read_text())
    if any(x['status'].startswith('blocked') for x in results):
        raise ValueError('Do not silently omit blocked materials: '+str([(x['material'],x.get('error')) for x in results if x['status'].startswith('blocked')]))
    materials=[dict(source=r['sample'],output=OUTPUT+'/Materials/'+r['material'],report=str(A/'material-validation'/(r['material']+'.json')))
               for r in results]
    meshes={}
    expected=set()
    for package in (A/'effect-export').rglob('*.srprefab'):
        with zipfile.ZipFile(package) as archive:manifest=json.loads(archive.read('manifest.json'))
        expected.update((m['SourceCAB'],m['PathID']) for m in manifest['Materials'])
        for entry in manifest['Meshes']:
            if entry['Key'] in meshes:continue
            file=A/'effect-closure/inputs'/entry['SourceCAB']
            obj=next(o for o in load_container(file).objects if o.type.value==43 and o.path_id==entry['PathID'])
            tree=obj.read_typetree(check_read=True);handler=MeshHandler(obj.read());handler.process()
            if len(handler.m_Vertices)!=entry['VertexCount']:raise ValueError('Mesh vertex count mismatch')
            record=dict(name=tree['m_Name'],vertices=handler.m_Vertices,normals=handler.m_Normals or [],tangents=handler.m_Tangents or [],
                colors=handler.m_Colors or [],colorFormat=tree['m_VertexData']['m_Channels'][3]['format'],
                uvs=[getattr(handler,'m_UV'+str(i)) or [] for i in range(8)],
                triangles=[[v for tri in sub for v in tri] for sub in handler.get_triangles()])
            meshes[entry['Key']]=dict(key=entry['Key'],sourceFile=str(file),sourceFileSha256=sha(file),
                sourceObjectSha256=hashlib.sha256(obj.get_raw_data()).hexdigest(),typeHash=obj.serialized_type.old_type_hash.hex(),data=record)
    if expected!={(r['sourceFile'],r['pathId']) for r in results}:raise ValueError('Material results do not exactly cover source prefab references')
    names={}
    for mesh in meshes.values():
        name=mesh['data']['name'];names[name]=names.get(name,0)+1
        suffix='' if names[name]==1 else '_'+str(names[name])
        if name!=Path(name).name:raise ValueError('Unsafe source mesh name')
        mesh['asset']=OUTPUT+'/Model/EffectMeshes/'+name+suffix+'.asset'
    plan=dict(schema='my-hook.skill-resources.v1',materials=materials,meshes=list(meshes.values()),
              meshReport=str(A/'native-effect-mesh-bindings.json'),jobReport=str(A/'resources-unity.json'))
    path=A/'resource-plan.json';path.write_text(json.dumps(plan,indent=2),encoding='utf-8')
    print(json.dumps(dict(plan=str(path),materials=len(materials),meshes=len(meshes))))


if __name__=='__main__':main()
