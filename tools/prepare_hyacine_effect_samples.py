"""Rehost only materials actually referenced by the five source Skill01 hit prefabs."""
import contextlib
import hashlib
import io
import json
import re
import subprocess
import sys
import zipfile
import argparse
from pathlib import Path
from types import SimpleNamespace
from hsr_static_container import load_container
from compare_prefab_schema_trace import compare
from build_static_shader_sample import generate, keyword_names
from prepare_character_reflection_samples import environment

ROOT=Path(__file__).resolve().parents[1]
V=ROOT/'artifacts/StaticShaderSamples/HSR-4.6'
A=V/'hyacine-attack-source'
CROSS=ROOT.parents[1]/'Tools/_shader_reconstruction/SPIRV-Cross-build/Release/spirv-cross.exe'


def run(command):
    process=subprocess.run(list(map(str,command)),capture_output=True,text=True,encoding='utf-8',timeout=90)
    if process.returncode:raise ValueError((process.stdout+process.stderr)[-3000:])
    return process.stdout


def main(only_blocked=False):
    source=A/'effect-metadata-inputs'
    textures=A/'effect-textures';textures.mkdir(exist_ok=True)
    identity={}
    for p in source.iterdir():
        if p.suffix=='.resS' or p.name=='CAB-d140c6d290d70e92fb3a5e385720ea83':continue
        for obj in load_container(p).objects:
            if obj.type.value==28:
                data=obj.read_typetree(check_read=True)
                name=data['m_Name'];key=hashlib.sha256(obj.get_raw_data()).hexdigest()
                identity[(p.name,obj.path_id)]=key
                target=textures/p.name/str(obj.path_id);target.mkdir(parents=True,exist_ok=True)
                if data['m_TextureFormat']!=17:obj.read().image.save(target/(name+'.png'))
    packages=sorted((A/'effect-export').rglob('*.srprefab'))
    used={}
    for package in packages:
        name=package.stem
        comparison=A/'effect-comparison'/name/'comparison.json'
        if not comparison.exists():
            with contextlib.redirect_stdout(io.StringIO()):
                compare(package,A/'effect-closure/inputs',V/'schema-candidates/native45-particle-exact.structs.dump',comparison.parent)
        proof=json.loads(comparison.read_text())
        if not proof['passed'] or proof['packageSha256']!=hashlib.sha256(package.read_bytes()).hexdigest():raise ValueError('Independent source comparison failed')
        with zipfile.ZipFile(package) as archive:
            manifest=json.loads(archive.read('manifest.json'))
            for material in manifest['Materials']:
                used[(material['SourceCAB'],material['PathID'])]=material
        print(json.dumps(dict(comparison=name,components=len(proof['components']),passed=True)),flush=True)
    report=json.loads((A/'effect-metadata/shader-report.json').read_text(encoding='utf-8-sig'))
    previous={}
    if only_blocked:
        previous={(r['sourceFile'],r['pathId']):r for r in json.loads((A/'effect-shader-results.json').read_text())}
    results=[]
    for identity,material_source in used.items():
        if identity in previous and not previous[identity]['status'].startswith('blocked'):
            results.append(previous[identity]);continue
        material=next(x for x in report['materials'] if (x['source']['fileName'],x['pathId'])==identity)
        shader=next(x for x in report['shaders'] if x['pathId']==material['shader']['pathId'] and x['source']['fileName']==material['shader']['sourceFile'])
        shader_pass=shader['serialized']['subShaders'][0]['passes'][0]
        programs=[p for p in shader_pass['stages']['vertex']['subPrograms'] if p['gpuProgramType']=='SPIRV']
        universe={k for p in programs for k in keyword_names(p,shader_pass['nameIndices'])}
        active=set(re.findall(r'[^,\s]+',material['shaderKeywords']))&universe
        choices=[p for p in programs if set(keyword_names(p,shader_pass['nameIndices']))==active]
        record=dict(sourceFile=identity[0],pathId=identity[1],material=material['name'],shader=shader['name'])
        try:
            if len({p['blobIndex'] for p in choices})!=1:raise ValueError('Ambiguous source pass0 vertex variant')
            program=choices[0];index=program['blobIndex']
            program_dir=A/'effect-programs'/(shader['name'].split('/')[-1]+'_'+str(index))
            if not (program_dir/'decoded/snippet-decode.json').exists():
                run([sys.executable,ROOT/'tools/extract_apk_shader_program.py','--shader-report',A/'effect-metadata/shader-report.json',
                     '--containers',source,'--shader-name',shader['name'],'--output',program_dir,'--opaque-record-word','--program-indices',index])
                run([ROOT/'src/StaticShaderDecode/bin/Release/net8.0/StaticShaderDecode.exe',program_dir/f'Vulkan_{index}.program.bin',program_dir/'decoded'])
            contract=environment(program,shader_pass['nameIndices'],{p['name'] for p in shader['serialized']['properties']},program_dir/'decoded')
            contract['purpose']='Declared empty-scene fixture for original pass zero; not recovered runtime environment.'
            env=program_dir/'environment.json';env.write_text(json.dumps(contract,indent=2),encoding='utf-8')
            output=A/'effect-samples'/material['name']
            args=SimpleNamespace(shader_report=A/'effect-metadata/shader-report.json',containers=source,programs=program_dir,
                decoded=program_dir/'decoded',cross=CROSS,material_name=material['name'],program_index=index,texture=None,
                textures=textures,reuse_shader=None,output=output,environment=env,pass_lightmode=None,exact_pass_state=True)
            with contextlib.redirect_stdout(io.StringIO()):generate(args)
            record.update(status='original_pass0_rehosted_explicit_scene_fixture',sample=str(output/'sample.json'))
        except Exception as error:
            record.update(status='blocked_no_fallback_generated',error=str(error))
        results.append(record)
        (A/'effect-shader-results.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
        print(json.dumps(record),flush=True)
    (A/'effect-shader-results.json').write_text(json.dumps(results,indent=2),encoding='utf-8')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--only-blocked',action='store_true')
    main(parser.parse_args().only_blocked)
