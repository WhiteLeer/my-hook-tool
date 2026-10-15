"""Recover selected original reflection-pass programs with an explicitly artificial preview environment."""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace
from hsr_static_container import load_container
from build_static_shader_sample import generate, keyword_names, reflect

ROOT=Path(__file__).resolve().parents[1]
C=ROOT/'artifacts/StaticShaderSamples/HSR-4.6/character-preview-source'
CROSS=ROOT.parents[1]/'Tools/_shader_reconstruction/SPIRV-Cross-build/Release/spirv-cross.exe'


def run(command):
    r=subprocess.run(list(map(str,command)),capture_output=True,text=True,encoding='utf-8',timeout=90)
    if r.returncode:raise ValueError(r.stdout+r.stderr)
    return r.stdout


def environment(program,names,properties,decoded):
    inverse={v:k for k,v in names.items()}
    p=program['parameters']
    blocks={((b['index']>>16)&255,b['index']&65535):next(x for x in p['constantBuffers'] if x['nameIndex']==b['nameIndex']) for b in p['constantBufferBindings']}
    params={}
    engines={'unity_ObjectToWorld','unity_WorldToObject','unity_MatrixMVP','unity_MatrixVP','unity_MatrixV',
             '_ProjectionParams','_ZBufferParams','_WorldSpaceCameraPos','unity_WorldTransformParams','_NonJitteredViewProjMatrix','unity_MatrixInvP','unity_MatrixInvV','_ScaledScreenParams'}
    for snippet in decoded.glob('*.spv'):
        reflection=reflect(CROSS,snippet)
        source=run([CROSS,snippet,'--hlsl','--shader-model','50'])
        body=re.sub(r'cbuffer\s+\w+\s*:\s*register\([^)]*\)\s*\{.*?\};','',source,flags=re.S)
        for span in re.finditer(r'cbuffer\s+(\w+)\s*:\s*register\([^)]*\)\s*\{(.*?)\};',source,re.S):
            ub=next(x for x in reflection['ubos'] if x['name']==span[1]);block=blocks[(ub['set'],ub['binding'])]
            for line in span[2].splitlines():
                d=re.search(r'(\w+)(\[\d+\])? : packoffset\(c(\d+)(?:\.([xyzw]))?\)',line)
                if not d or not re.search(r'\b'+d[1]+r'\b',body):continue
                offset=int(d[3])*16+('xyzw'.index(d[4])*4 if d[4] else 0)
                param=next(x for x in block['vectors']+block['matrices'] if x['index']==offset)
                name=inverse[param['nameIndex']]
                if name in properties or name.endswith('_ST') and name[:-3] in properties or name in engines:continue
                if param in block['matrices']:
                    if name!='_ES_GlobalRotMatrix':raise ValueError('Unspecified scene matrix '+name)
                    continue
                dimension=param['dimension'];kind='Float' if dimension==1 else 'Vector' if dimension==4 else 'Vector'+str(dimension)
                value=0 if dimension==1 else [0]*dimension
                if name in {'_ES_CharacterShadowFactor','_ES_HeightLerpTop','_ES_LevelHighLight','_ES_FogFar','_ES_HeightFogRange','_ES_HeightFogFogFar'}:value=1
                if name=='_ES_LevelMid':value=0.5
                if name in {'_ES_FogFar','_ES_HeightFogFogFar','_FarClip'}:value=100
                if name.endswith('Color') or name in {'_ES_LevelSkinLightColor','_ES_LevelSkinShadowColor','_ES_LevelHighLightColor','_ES_LevelShadowColor','_ES_HeightLerpTopColor','_ES_HeightLerpMiddleColor','_ES_HeightLerpBottomColor'}:
                    if dimension>1:value=[1]*dimension
                if name=='_MainLightPosition':value=[0.4,0.8,-0.6,0]
                params[name]=dict(type=kind,value=value,reason='Declared isolated neutral scene fixture, NOT recovered runtime value.')
        for texture in reflection.get('separate_images',[]):
            t=next(x for x in p['textures'] if (((x['index']>>16)&255),x['index']&65535)==(texture['set'],texture['binding']))
            name=inverse[t['nameIndex']]
            if name not in properties:
                dimension=texture['type']
                if dimension not in ('texture2D','texture3D'):raise ValueError('Unsupported scene resource dimensionality')
                params[name]=dict(type='Texture3D' if dimension=='texture3D' else 'Texture',value='black' if dimension=='texture3D' else 'white',
                    reason='Explicit empty-scene resource, NOT original game depth/fog. Volume uses (0,0,0,1); volume enable is zero in this fixture.')
    params['$inlineSampler84']=dict(type='ExplicitPreviewOverride',value='PointClamp',reason='Raw sampler word preserved; this preview state is NOT asserted to be its decoded meaning.')
    if any(x.get('type')=='Texture3D' for x in params.values()):
        params['$inlineSampler85']=dict(type='ExplicitPreviewOverride',value='PointClamp',
            reason='Constant one-voxel empty-scene volume: filtering cannot change its value. Original raw sampler 85 remains uninterpreted.')
    return dict(schema='my-hook.isolated-character-reflection.v1',status='preview_fixture_not_recovered_game_state',
                purpose='Original reflection pass only; NOT the main deferred character render or game visual equivalence.',parameters=params)


def main(a):
    source=C/'hyacine-closure/inputs';inputs=C/'metadata-inputs';textures=C/'texture-exports'
    textures.mkdir(exist_ok=True)
    for path in source.iterdir():
        shutil.copyfile(path,inputs/path.name)
        if path.suffix=='.resS':continue
        for obj in load_container(path).objects:
            if obj.type.value==28:
                tex=obj.read_typetree(check_read=True)
                if tex['m_TextureFormat']!=17:obj.read().image.save(textures/(tex['m_Name']+'.png'))
    ids=','.join(map(str,json.loads((C/'selected-shader-ids.json').read_text())))
    print(run([sys.executable,ROOT/'tools/export_static_shader_metadata.py','--as-cli',ROOT.parents[1]/'Base_AS/AnimeStudio-master/AnimeStudio.CLI/bin/Release/net8.0-windows/AnimeStudio.CLI.exe',
               '--source',inputs,'--output',C/'metadata','--shader-path-ids='+ids]).strip())
    report=json.loads((C/'metadata/shader-report.json').read_text(encoding='utf-8-sig'))
    for material_name in a.materials:
        material=next(x for x in report['materials'] if x['name']==material_name)
        shader=next(s for s in report['shaders'] if s['pathId']==material['shader']['pathId'])
        shader_pass=next(p for p in shader['serialized']['subShaders'][0]['passes'] if p['state']['tags'].get('LIGHTMODE')=='CustomReflection')
        programs=[v for v in shader_pass['stages']['vertex']['subPrograms'] if v['gpuProgramType']=='SPIRV']
        universe=set(k for v in programs for k in keyword_names(v,shader_pass['nameIndices']))
        kw=set(re.findall(r'[^,\s]+',material['shaderKeywords']))&universe
        choices=[p for p in programs if set(keyword_names(p,shader_pass['nameIndices']))==kw]
        if len({p['blobIndex'] for p in choices})!=1:raise ValueError('Ambiguous explicit reflection fixture variant')
        program=choices[0];index=program['blobIndex']
        output=C/'reflection-samples'/material_name;program_dir=C/'reflection-programs'/(shader['name'].split('/')[-1]+'_'+str(index))
        print(run([sys.executable,ROOT/'tools/extract_apk_shader_program.py','--shader-report',C/'metadata/shader-report.json',
                   '--containers',inputs,'--shader-name',shader['name'],'--output',program_dir,'--opaque-record-word','--program-indices',index]).strip())
        decoded=program_dir/'decoded'
        print(run([ROOT/'src/StaticShaderDecode/bin/Release/net8.0/StaticShaderDecode.exe',program_dir/f'Vulkan_{index}.program.bin',decoded]).strip())
        contract=environment(program,shader_pass['nameIndices'],{p['name'] for p in shader['serialized']['properties']},decoded)
        program_dir.mkdir(exist_ok=True);env=program_dir/'environment.json';env.write_text(json.dumps(contract,indent=2),encoding='utf-8')
        args=SimpleNamespace(shader_report=C/'metadata/shader-report.json',containers=inputs,programs=program_dir,
            decoded=decoded,cross=CROSS,material_name=material_name,program_index=index,texture=None,textures=textures,
            reuse_shader=None,output=output,environment=env,pass_lightmode='CustomReflection')
        generate(args)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--materials',nargs='+',default=['Hyacine_01_Mat_Body_S','Hyacine_01_Mat_Body_D','Hyacine_01_Mat_Weapon','Hyacine_01_Mat_Hair','Hyacine_00_Mat_Face'])
    main(p.parse_args())
