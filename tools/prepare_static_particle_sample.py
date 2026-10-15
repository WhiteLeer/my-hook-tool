"""Prepare a bounded particle-only plan using exact material source identities."""
import argparse
import hashlib
import json
import zipfile
from pathlib import Path


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prepare(package,comparison,project,samples,output,preview=None):
    evidence=json.loads(comparison.read_text())
    if not evidence['passed'] or evidence['packageSha256']!=digest(package):
        raise ValueError('Package does not match independent comparison')
    with zipfile.ZipFile(package) as archive:
        manifest=json.loads(archive.read('manifest.json'))
        particle_paths={n['Path'] for n in manifest['Nodes'] if any(c['Type']=='ParticleSystem' for c in n['Components'])}
        included={n['Path'] for n in manifest['Nodes'] if any(p==n['Path'] or p.startswith(n['Path']+'/') for p in particle_paths)}
        indexed={(x['sourceFile'],x['pathId'],x['type']):x for x in evidence['components']}
        nodes=[]
        omissions=[]
        for node in manifest['Nodes']:
            if node['Path'] not in included:
                omissions.append({'path':node['Path'],'reason':'non_particle_UI_node'})
                continue
            transforms=[c for c in node['Components'] if c['Type'] in ('Transform','RectTransform')]
            if len(transforms)!=1:
                raise ValueError('Each node must have one transform')
            components=[]
            for component in node['Components']:
                if component['Type'] not in ('ParticleSystem','ParticleSystemRenderer'):
                    if component['Type'] not in ('Transform','RectTransform'):
                        omissions.append({'path':node['Path'],'type':component['Type'],'metadata':component.get('MonoBehaviour'),
                                          'reason':'particle_only_preview_not_full_UI_reconstruction'})
                    continue
                identity=(component['SourceCAB'],component['PathID'],component['Type'])
                if identity not in indexed or not indexed[identity]['ownerPPtrVerified']:
                    raise ValueError('Component was not independently compared')
                values=json.loads(archive.read(component['ParametersFile']))
                trace=json.loads((comparison.parent/(component['SourceCAB']+'_'+str(component['PathID'])+'.json')).read_text())
                source_fields={f['path'].split('.')[1] for f in trace['fields'] if f['path'].startswith('Base.')}
                components.append(dict(type=component['Type'],pathId=component['PathID'],
                    sourceFile=component['SourceCAB'],parameterFile=component['ParametersFile'],data={k:v for k,v in values.items() if k in source_fields},
                    excludedAsConvenienceFields=sorted(set(values)-source_fields),
                    materialPointers=(component.get('ParticleRenderer') or {}).get('MaterialPointers',[])))
                components[-1]['sourceReferences']={str(p['fileId'])+':'+str(p['pathId']):p['resolution']
                    for p in indexed[identity].get('pointers',[]) if p['pathId'] and p['resolution'].get('status')=='target_class_verified'}
            nodes.append(dict(name=node['Name'],path=node['Path'],pathId=node['PathID'],sourceFile=manifest['SourceCAB'],
                              transform=json.loads(archive.read(transforms[0]['ParametersFile'])),components=components,
                              gameObject=next((n for n in evidence.get('nodes',[]) if n['path']==node['Path']),None)))
            nodes[-1]['transformSource']={'sourceFile':transforms[0]['SourceCAB'],'pathId':transforms[0]['PathID']}
        materials=[]
        for material in manifest['Materials']:
            matches=[]
            for sample_file in samples.rglob('sample.json'):
                sample=json.loads(sample_file.read_text())
                if sample.get('sourceMaterialFile')==material['SourceCAB'] and sample.get('sourceMaterialPathId')==material['PathID']:
                    matches.append((sample_file,sample))
            if len(matches)!=1:
                raise ValueError('Exact material identity must resolve uniquely')
            sample_file,sample=matches[0]
            if sample['sourceShaderFile']!=material['ShaderSourceCAB'] or sample['sourceShaderPathId']!=material['ShaderPathID']:
                raise ValueError('Shader PPtr disagrees with verified sample')
            material_file=sample_file.parent/(sample['name']+'.mat')
            if not material_file.is_file():
                raise ValueError('Verified material asset is absent')
            shader_asset=sample.get('sharedShaderAssetPath') or (sample_file.parent/sample['shaderPath']).relative_to(project).as_posix()
            shader_sha=digest(project/shader_asset)
            if sample.get('shaderSha256',shader_sha)!=shader_sha:
                raise ValueError('Recorded Shader bytes changed')
            materials.append(dict(key=material['Key'],asset=material_file.relative_to(project).as_posix(),
                sourceData=material,
                assetSha256=digest(material_file),sample=sample_file.relative_to(project).as_posix(),sampleSha256=digest(sample_file),
                shaderAsset=shader_asset,shaderSha256=shader_sha))
        plan=dict(schema='my-hook.static-particle-preview.v1',name=manifest['Name'],package=str(package.resolve()),
            packageSha256=digest(package),comparison=str(comparison.resolve()),comparisonSha256=digest(comparison),
            candidateSha256=evidence['candidateSha256'],nodes=nodes,materials=materials,omissions=omissions,
            scope='Particle-only experimental preview. RectTransform TRS retained, anchors/Canvas/UIParticle/SmoothMask not reconstructed.',
            times=[0.5,1.0,2.0,4.0],previewSeed=13217)
        if preview:
            settings=json.loads(preview.read_text())
            if settings['displayMode']!='native':
                raise ValueError('Explicit battle preview requires native particle rendering')
            plan.update(settings)
            plan['scope']='Native particle subtree with exact serialized transforms/material links. Gameplay MonoEffect/Follow behavior and scene environment are NOT reconstructed.'
            for omission in omissions:
                omission['reason']='gameplay_script_not_reconstructed' if omission.get('type')=='MonoBehaviour' else 'outside_particle_subtree'
    output.parent.mkdir(parents=True,exist_ok=True)
    output.write_text(json.dumps(plan,indent=2),encoding='utf-8')
    print(json.dumps(dict(nodes=len(nodes),particles=sum(c['type']=='ParticleSystem' for n in nodes for c in n['components']),
                          materials=len(materials),omissions=len(omissions))))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for name in ('package','comparison','project','samples','output'):
        p.add_argument('--'+name,type=Path,required=True)
    p.add_argument('--preview',type=Path)
    a=p.parse_args()
    prepare(a.package,a.comparison,a.project,a.samples,a.output,a.preview)
