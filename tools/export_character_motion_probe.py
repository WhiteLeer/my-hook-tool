"""Export a bounded Art root and controller-referenced full-body motions; keep all logs external."""
from pathlib import Path
import json
import shutil
import subprocess

ROOT=Path(__file__).resolve().parents[1]
C=ROOT/'artifacts/StaticShaderSamples/HSR-4.6/character-preview-source'
inputs=C/'motion-inputs';inputs.mkdir(exist_ok=True)
for folder in ['hyacine-closure/inputs','controller-closure/inputs']:
    for p in (C/folder).iterdir():shutil.copyfile(p,inputs/p.name)
cli=ROOT.parents[1]/'Base_AS/AnimeStudio-master/AnimeStudio.CLI/bin/Release/net8.0-windows/AnimeStudio.CLI.exe'
command=[str(cli),str(inputs),str(C/'model-motion-export'),'--game','SR','--batch_load','--export_type','FBX',
         '--group_assets','ByModel','--types','GameObject:Both','AnimationClip:Both','--names',
         r'^(Art_Hyacine_01|Avatar_Hyacine_00_Adv_Ani_(Common_IdleShow|StandBy))$',
         '--logger_flags','Info','Warning','Error']
r=subprocess.run(command,capture_output=True,text=True,encoding='utf-8',timeout=90)
(C/'model-motion-export.log').write_text(r.stdout+r.stderr,encoding='utf-8')
print(json.dumps(dict(exitCode=r.returncode,files=[dict(file=str(x.relative_to(C)),bytes=x.stat().st_size)
    for x in (C/'model-motion-export').rglob('*') if x.is_file()])))
if r.returncode or '[Error]' in r.stdout:raise ValueError('Inspect model-motion-export.log before accepting output')
