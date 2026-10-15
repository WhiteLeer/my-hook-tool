"""Prepare the selected character's material and Shader inputs without broad Shader extraction."""
from pathlib import Path
import json
import os
import shutil
from hsr_static_container import load_container

ROOT=Path(__file__).resolve().parents[1]
B=ROOT/'artifacts/StaticShaderSamples/HSR-4.6'
C=B/'character-preview-source'
source=C/'hyacine-closure/inputs'
target=C/'metadata-inputs'
target.mkdir(exist_ok=True)
ids=set()
for p in source.iterdir():
    if p.suffix=='.resS':continue
    e=load_container(p)
    materials=[o for o in e.objects if o.type.value==21]
    if materials:
        shutil.copyfile(p,target/p.name)
        for o in materials:
            m=o.read_typetree(check_read=True);ids.add(m['m_Shader']['m_PathID'])
            print(m['m_Name'],m['m_ShaderKeywords'])
shader=B/'battle-effect-source/shader-library/containers/CAB-d140c6d290d70e92fb3a5e385720ea83'
if not (target/shader.name).exists():os.link(shader,target/shader.name)
(C/'selected-shader-ids.json').write_text(json.dumps(sorted(ids)),encoding='utf-8')
print('ShaderIDs',','.join(map(str,sorted(ids))))
