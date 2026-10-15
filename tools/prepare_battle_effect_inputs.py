"""Assemble only the exact CAB/PPtr closure already verified by directory evidence."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
B = ROOT / 'artifacts/StaticShaderSamples/HSR-4.6/battle-effect-source'


def main():
    target = B / 'hit-prefab-inputs'
    target.mkdir(exist_ok=True)
    sources = [
        B/'discovery-early/probe/b3211bbda94a938c70a6ebc9c14fb8bf/containers/CAB-b81cc79b7f4519311f1bae1bafad12bf',
        B/'hit-material-block/probe/443fae292a8a6d1199ddd780f8f64295/containers/CAB-e9d89b566595430af54146d3c2618685',
        B/'hit-texture-resources/containers/CAB-01f25540e4db4735f715b3553f22b1e3',
        B/'hit-texture-resources/containers/CAB-01f25540e4db4735f715b3553f22b1e3.resS',
    ]
    for source in sources:
        shutil.copyfile(source, target/source.name)
    # Texture decoder needs its external resource sibling; metadata provenance
    # covers serialized inputs only and is unchanged by this resource copy.
    resource = sources[-1]
    shutil.copyfile(resource, B/'hit-metadata-inputs'/resource.name)
    command = [str(ROOT.parents[1]/'Base_AS/AnimeStudio-master/AnimeStudio.CLI/bin/Release/net8.0-windows/AnimeStudio.CLI.exe'),
               str(target), str(B/'hit-export'), '--game', 'SR', '--batch_load', '--export_type', 'Prefab',
               '--group_assets', 'BySource', '--types', 'GameObject', '--names', '^Eff_Avatar_Gilgamesh_00_Skill02_hit$',
               '--type_tree_dump', str(B.parent/'schema-candidates/native45-particle-exact.structs.dump'),
               '--force_type_tree_classes', '198,199', '--logger_flags', 'Info', 'Warning', 'Error']
    result = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', timeout=90)
    (B/'hit-prefab-export.log').write_text(result.stdout+result.stderr, encoding='utf-8')
    if result.returncode:
        raise ValueError('AS export failed; inspect artifact log')
    import sys
    sys.path.insert(0,str(ROOT/'tools'))
    from hsr_static_container import load_container
    env=load_container(target/sources[-2].name)
    obj=next(x for x in env.objects if x.path_id==-8639642188977018165)
    tex=obj.read_typetree()
    png=B/'Eff_Glow_301.png'
    obj.read().image.save(png)
    print(json.dumps({'texture':tex['m_Name'],'mips':tex['m_MipCount'],'format':tex['m_TextureFormat'],
                      'stream':tex['m_StreamData'],'png':str(png),
                      'packages':[str(x) for x in (B/'hit-export').rglob('*.srprefab')]}))


if __name__=='__main__':
    main()
