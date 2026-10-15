"""Inspect active original character-program bindings by exact descriptor and byte offset."""
from pathlib import Path
import json
import re
import subprocess

ROOT=Path(__file__).resolve().parents[1]
C=ROOT/'artifacts/StaticShaderSamples/HSR-4.6/character-preview-source'
cross=ROOT.parents[1]/'Tools/_shader_reconstruction/SPIRV-Cross-build/Release/spirv-cross.exe'
r=json.loads((C/'metadata/shader-report.json').read_text(encoding='utf-8-sig'))
s=next(x for x in r['shaders'] if x['name'].endswith('CharacterBase'))
p=s['serialized']['subShaders'][0]['passes'][1]
v=next(x for x in p['stages']['vertex']['subPrograms'] if x['gpuProgramType']=='SPIRV' and x['blobIndex']==1281)
names={v:k for k,v in p['nameIndices'].items()}
bindings={((b['index']>>16)&255,b['index']&65535):next(x for x in v['parameters']['constantBuffers'] if x['nameIndex']==b['nameIndex']) for b in v['parameters']['constantBufferBindings']}
for snippet in (C/'base-decoded').glob('*.spv'):
    refl=json.loads(subprocess.check_output([str(cross),str(snippet),'--reflect'],text=True))
    mode=refl['entryPoints'][0]['mode']
    hlsl=subprocess.check_output([str(cross),str(snippet),'--hlsl','--shader-model','50'],text=True)
    (C/'base-programs'/('Original-'+mode+'.hlsl')).write_text(hlsl,encoding='utf-8')
    active=[]
    body=re.sub(r'cbuffer\s+\w+\s*:\s*register\([^)]*\)\s*\{.*?\};','',hlsl,flags=re.S)
    for block in re.finditer(r'cbuffer\s+(\w+)\s*:\s*register\([^)]*\)\s*\{(.*?)\};',hlsl,re.S):
        rb=next(x for x in refl['ubos'] if x['name']==block[1]);b=bindings[(rb['set'],rb['binding'])]
        for line in block[2].splitlines():
            d=re.search(r'(\w+)(\[\d+\])? : packoffset\(c(\d+)(?:\.([xyzw]))?\)',line)
            if not d or not re.search(r'\b'+d[1]+r'\b',body):continue
            offset=int(d[3])*16+('xyzw'.index(d[4])*4 if d[4] else 0)
            param=next(x for x in b['vectors']+b['matrices'] if x['index']==offset)
            active.append(dict(source=names[param['nameIndex']],declaration=line.strip(),parameter=param))
    print(mode,'inputs',refl.get('inputs'),'outputs',refl.get('outputs'))
    print(mode,'active',[(x['source'],x['parameter'].get('arraySize')) for x in active])
    print(mode,'textures',[(names[x['nameIndex']],x['dimension']) for x in v['parameters']['textures']])
    (C/'base-programs'/('active-'+mode+'.json')).write_text(json.dumps(dict(reflection=refl,active=active),indent=2),encoding='utf-8')
