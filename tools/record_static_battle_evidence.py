"""Record reproducible source/preview evidence for the selected gameplay experiment."""
import argparse
import asyncio
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path
from probe_guest_asset_blocks import rows

ROOT=Path(__file__).resolve().parents[1]
B=ROOT/'artifacts/StaticShaderSamples/HSR-4.6/battle-effect-source'


def sha(path,algorithm='sha256'):
    with path.open('rb') as stream: return hashlib.file_digest(stream,algorithm).hexdigest()


def read(path): return json.loads(path.read_text(encoding='utf-8-sig'))


async def unity_evidence(url,instance):
    from mcp import ClientSession
    from mcp.client.streamable_http import streamable_http_client
    from unity_mcp_call import require_success
    async with streamable_http_client(url) as (reader,writer,_):
        async with ClientSession(reader,writer) as session:
            await session.initialize()
            require_success(await session.call_tool('set_active_instance',{'instance':instance}))
            contracts=await session.call_tool('execute_custom_tool',{'tool_name':'build_static_particle_sample',
                'parameters':{'action':'curve_contracts','plan':'unused','output':'unused','report':'unused'}})
            require_success(contracts)
            (B/'unity-curve-contracts.json').write_text(contracts.model_dump_json(),encoding='utf-8')
            state=await session.read_resource('mcpforunity://editor/state')
            (B/'unity-editor-state.json').write_text(state.model_dump_json(),encoding='utf-8')


def main(a):
    result=subprocess.run([str(a.adb),'-P','5037','-s',a.serial,'shell','dumpsys','package','com.miHoYo.hkrpg'],
                          capture_output=True,text=True,timeout=15,check=True)
    (B/'client-package.txt').write_text(result.stdout,encoding='utf-8')
    version=re.search(r'versionName=(\S+)',result.stdout).group(1)
    code=re.search(r'versionCode=(\d+)',result.stdout).group(1)
    if version!='4.6.0' or code!='118': raise ValueError('Client version changed; do not silently reuse this experiment')
    table=B/'catalog/BlockV_cdf91f78dcbfe53d593c8c58bd76816e.bytes'
    entries={r['md5']:r for r in rows(table)}
    blocks=[]
    paths=[B/'discovery-early/blocks/b3211bbda94a938c70a6ebc9c14fb8bf.block',
        B/'hit-material-block/blocks/443fae292a8a6d1199ddd780f8f64295.block',
        B/'hit-texture-block/blocks/3283703f141f67c8b1779843a637f918.block',
        B/'discovery-large/blocks/ee72b90d1353792c0321d0462c253a2d.block']
    for path in paths:
        record=entries[path.stem]
        if path.stat().st_size!=record['size'] or sha(path,'md5')!=record['md5']:
            raise ValueError('BLOCK differs from current table')
        blocks.append(dict(file=str(path.relative_to(ROOT)),tableRow=record['row'],tableByteOffset=record['offset'],
                           size=record['size'],md5=record['md5'],sha256=sha(path)))
    comparison=read(B/'hit-comparison/comparison.json')
    preview=read(B/'hit-particle-unity.json')
    sample=read(B/'hit-sample/sample.json')
    if not comparison['passed'] or preview['status']=='preview_failed': raise ValueError('Acceptance evidence failed')
    tests=[]
    for suite in ['test_static_battle_sample.py','test_static_shader_extraction.py','test_prefab_schema_comparison.py','test_trace_serialized_schema.py']:
        completed=subprocess.run([sys.executable,'-m','unittest','discover','-s','tools','-p',suite,'-v'],
                                 cwd=ROOT,capture_output=True,text=True,timeout=90)
        tests.append(dict(suite=suite,exitCode=completed.returncode,output=completed.stdout+completed.stderr))
        if completed.returncode: raise ValueError('Regression failed: '+suite+'\n'+completed.stderr)
    (B/'regression-tests.json').write_text(json.dumps(tests,indent=2),encoding='utf-8')
    asyncio.run(unity_evidence(a.url,a.instance))
    ledger=dict(schema='my-hook.static-battle-evidence.v1',date='2026-10-15',
        client=dict(version=version,versionCode=int(code),evidence=str((B/'client-package.txt').relative_to(ROOT))),
        subject='Eff_Avatar_Gilgamesh_00_Skill02_hit',
        confirmed=dict(blockTableSha256=sha(table),blocks=blocks,
            root='CAB-b81cc79b7f4519311f1bae1bafad12bf#-6617258281910355763',
            material=f"{sample['sourceMaterialFile']}#{sample['sourceMaterialPathId']}",
            shader=f"{sample['sourceShaderFile']}#{sample['sourceShaderPathId']}",
            shaderName=sample['sourceShaderName'],keywords=sample['keywords'],
            programIndices=dict(vertex=sample['vertexMetadataBlobIndex'],fragment=sample['fragmentMetadataBlobIndex']),
            comparedSourceValues=sum(c['comparedValues'] for c in comparison['components']),
            components=len(comparison['components']),mismatches=0,
            evidence=['hit-comparison/comparison.json','hit-metadata/metadata-provenance.json','hit-programs/program-extraction.json',
                'hit-decoded/snippet-decode.json','hit-sample/sample.json','unity-curve-contracts.json']),
        observed=dict(status=preview['status'],savedSourceTransforms=preview['savedSourceTransformsReadbackPassed'],
            savedMappedFields=preview['savedMappedFieldsReadbackPassed'],
            roundtripAssertions=sum(len(c['checks']) for c in preview['mappings']),
            deterministic=preview['deterministic'],sceneReloadMatches=preview['sceneReloadMatches'],
            frames=[dict(time=f['time'],changedPixels=f['changedPixels'],magentaPixels=f['magentaPixels']) for f in preview['frames']],
            scene=preview['scene'],prefab=preview['prefab'],regressionTests='regression-tests.json'),
        hypothesis=['Historical TypeTree compatibility is proved structurally only for these selected objects, not all 4.6 semantics.'],
        blocked=['Game MonoEffect/Follow target, enum semantics, offsets and time control are not reconstructed.',
            'Unknown active-camera environment values and original postprocessing prevent game pixel-fidelity claims.',
            'Empty scene depth fixture does not validate soft-depth intersection or stencil behavior.',
            'Custom native renderer/particle fields remain itemized in hit-particle-unity.json; no complete-mapping claim.',
            'No runtime Material/Shader to RenderDoc EID bridge or Timeline reconstruction in this experiment.'],
        sourceBoundary='Serialized CAB#PathID references only. Names discover candidates, not object identity. No injection or game-process hooks used.',
        artifactsBoundary='Raw packages, binary programs, logs and JSON remain outside Unity Assets; only generated sample manifest accompanies material assets.')
    target=B/'evidence-ledger.json'
    target.write_text(json.dumps(ledger,indent=2),encoding='utf-8')
    print(json.dumps(dict(ledger=str(target),status=preview['status'],tests='all passed')))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--adb',type=Path,required=True)
    p.add_argument('--serial',required=True)
    p.add_argument('--url',required=True)
    p.add_argument('--instance',required=True)
    main(p.parse_args())
