"""Bind the exported rig to source Renderer identities, ordered bone PPtrs and material slots."""
import hashlib
import json
import zipfile
import re
import zlib
from pathlib import Path
from hsr_static_container import load_container
from UnityPy.helpers.MeshHelper import MeshHandler

ROOT=Path(__file__).resolve().parents[1]
C=ROOT/'artifacts/StaticShaderSamples/HSR-4.6/character-preview-source'
root_cab='CAB-794dfea0a9c919941b1acb0ebf584600'
OMITTED_MATERIALS={
    'CAB-a62ffe0c3fe223f92914adeee4c8f648:3727209815991833530',
    'CAB-b109a864445d54dfe27e7af888d4a106:3082673868023183515',
}


def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()


def resolve_blendshape_attributes(source,destination,channel_names):
    names={}
    for name in channel_names:
        key=zlib.crc32(name.encode('utf-8'))
        if key in names and names[key]!=name:raise ValueError('Ambiguous source blendshape CRC32; do not guess')
        names[key]=name
    replacements={}
    with source.open(encoding='utf-8') as src,destination.open('w',encoding='utf-8',newline='\n') as dst:
        for line in src:
            match=re.fullmatch(r'(\s*attribute: blendShape\.)(\d+)\s*\n?',line)
            if match and int(match[2]) in names:
                name=names[int(match[2])];line=match[1]+name+'\n'
                replacements[match[2]]=dict(sourceChannelName=name,count=replacements.get(match[2],{}).get('count',0)+1)
            dst.write(line)
    return replacements


def main():
    package=next((C/'art-first').rglob('*.srprefab'))
    with zipfile.ZipFile(package) as z:manifest=json.loads(z.read('manifest.json'))
    nodes={(root_cab,n['PathID']):n['Path'].split('/',1)[1] if '/' in n['Path'] else '' for n in manifest['Nodes']}
    source_transforms=[]
    with zipfile.ZipFile(package) as z:
        for n in manifest['Nodes']:
            component=next(c for c in n['Components'] if c['Type']=='Transform')
            trs=json.loads(z.read(component['ParametersFile']))
            p=trs['LocalPosition'];q=trs['LocalRotation'];s=trs['LocalScale']
            source_transforms.append(dict(path=nodes[(root_cab,n['PathID'])],
                position=[p['X'],p['Y'],p['Z']],rotation=[q['X'],q['Y'],q['Z'],q['W']],scale=[s['X'],s['Y'],s['Z']]))
    transforms={(c['SourceCAB'],c['PathID']):nodes[(root_cab,n['PathID'])] for n in manifest['Nodes'] for c in n['Components'] if c['Type']=='Transform'}
    details=json.loads((C/'art-details/character-details.json').read_text())
    materials=json.loads((C/'unity-material-bindings.json').read_text())
    records=[];omissions=[];mesh_records={}
    for renderer in details:
        if renderer['sourceFile']!=root_cab:continue
        mesh_ref=renderer['mesh'];env=load_container(C/'hyacine-closure/inputs'/mesh_ref['sourceFile'])
        obj=next(x for x in env.objects if x.path_id==mesh_ref['pathId'] and x.type.value==43)
        mesh=obj.read_typetree(check_read=True)
        mesh_key=mesh_ref['sourceFile']+'#'+str(mesh_ref['pathId'])
        handler=MeshHandler(obj.read());handler.process()
        def native(v):return list(map(float,v))
        bindposes=[]
        for matrix in mesh['m_BindPose']:
            bindposes.append([float(matrix[f'e{r}{c}']) for r in range(4) for c in range(4)])
        shapes=[];shape_data=mesh['m_Shapes']
        for channel in shape_data['channels']:
            for i in range(channel['frameIndex'],channel['frameIndex']+channel['frameCount']):
                shape=shape_data['shapes'][i];deltas=[]
                for v in shape_data['vertices'][shape['firstVertex']:shape['firstVertex']+shape['vertexCount']]:
                    deltas.append(dict(index=v['index'],position=native([v['vertex'][k] for k in 'xyz']),
                        normal=native([v['normal'][k] for k in 'xyz']) if shape['hasNormals'] else [0,0,0],
                        tangent=native([v['tangent'][k] for k in 'xyz']) if shape['hasTangents'] else [0,0,0]))
                shapes.append(dict(name=channel['name'],weight=shape_data['fullWeights'][i],deltas=deltas))
        mesh_records[mesh_key]=dict(name=mesh['m_Name'],vertices=[native(v) for v in handler.m_Vertices],
            normals=[native(v) for v in handler.m_Normals] if handler.m_Normals else [],
            tangents=[native(v) for v in handler.m_Tangents] if handler.m_Tangents else [],
            colors=handler.m_Colors or [],colorFormat=mesh['m_VertexData']['m_Channels'][3]['format'],
            uvs=[getattr(handler,'m_UV'+str(i)) or [] for i in range(8)],
            weights=handler.m_BoneWeights,indices=handler.m_BoneIndices,bindposes=bindposes,
            triangles=[[index for triangle in sub for index in triangle] for sub in handler.get_triangles()],shapes=shapes,
            objectSha256=hashlib.sha256(obj.get_raw_data()).hexdigest(),basis='Original Unity coordinates throughout. No FBX mirror conversion.')
        slots=[];slot_sources=[]
        for i,pointer in enumerate(renderer['materials']):
            key=pointer['sourceFile']+':'+str(pointer['pathId'])
            if key not in materials:
                if key not in OMITTED_MATERIALS:raise ValueError('Unexpected unresolved material: '+key)
                slots.append(None)
                slot_sources.append(dict(sourceFile=pointer['sourceFile'],pathId=pointer['pathId'],omitted=True))
                omissions.append(dict(rendererPath=nodes[(root_cab,renderer['gameObject']['pathId'])],slot=i,
                    sourceMaterial=key,reason='Source stencil/eye GBuffer composition is not recovered in this isolated reflection-pass fixture. This draw is omitted, never replaced with a fallback material.'))
            else:
                material=materials[key];sample=Path(material['sample'])
                slots.append(material['asset'])
                slot_sources.append(dict(sourceFile=pointer['sourceFile'],pathId=pointer['pathId'],sample=str(sample),sampleSha256=sha(sample)))
        records.append(dict(path=nodes[(root_cab,renderer['gameObject']['pathId'])],sourceIdentity=root_cab+'#'+str(renderer['pathId']),
            meshSource=mesh_ref['sourceFile']+'#'+str(mesh_ref['pathId']),vertexCount=mesh['m_VertexData']['m_VertexCount'],
            bones=[transforms[(p['sourceFile'],p['pathId'])] for p in renderer['bones']],materials=slots,materialSources=slot_sources,
            rootBone=None,rootBoneStatus='Native parser did not expose rootBone; null in offscreen preview, not inferred.'))
    model=next((C/'model-motion-export/Models').rglob('*.fbx'))
    clip=next((C/'model-motion-export/Animations').rglob('*Common_IdleShow.anim'))
    derived=C/'native-motion';derived.mkdir(exist_ok=True)
    native_clip=derived/clip.name
    shape_mapping=resolve_blendshape_attributes(clip,native_clip,{s['name'] for mesh in mesh_records.values() for s in mesh['shapes']})
    mesh_file=C/'character-meshes.json';mesh_file.write_text(json.dumps(mesh_records,separators=(',',':')),encoding='utf-8')
    plan=dict(schema='my-hook.isolated-character-motion.v1',sourceRoot=root_cab+'#'+str(manifest['RootPathID']),
        meshes=str(mesh_file),meshesSha256=sha(mesh_file),
        sourcePackage=str(package),sourcePackageSha256=sha(package),model=str(model),modelSha256=sha(model),clip=str(native_clip),clipSha256=sha(native_clip),
        originalClip=str(clip),originalClipSha256=sha(clip),blendShapeAttributeResolution=shape_mapping,
        renderers=records,omissions=omissions,sourceTransforms=source_transforms,
        sceneFixture=dict(status='explicit_neutral_fixture_not_game_state',vectors={
            '_ES_AddColor':[0,0,0,0],
            '_ES_HeightLerpTopColor':[0.5,0.5,0.5,1],
            '_ES_HeightLerpMiddleColor':[0.5,0.5,0.5,1],
            '_ES_HeightLerpBottomColor':[0.5,0.5,0.5,1]},
            reason='Original program adds AddColor*diffuse, so neutral is zero. Height tint is multiplied by two, so neutral RGB is 0.5 at alpha 1. No game scene value is asserted.'),
        scope='Full-body pose and rig fixture with original CustomReflection programs; NOT the main deferred game pipeline.',
        sourceSchemaLimitations=['AnimationClip AC7 layout uses verified short indices but preserves an unclassified 4B footer.',
            'Material environment, inline sampler preview override and vertex register ABI remain explicitly experimental.'])
    target=C/'character-plan.json';target.write_text(json.dumps(plan,indent=2),encoding='utf-8')
    print(json.dumps(dict(plan=str(target),renderers=len(records),omissions=len(omissions))))


if __name__=='__main__':main()
