"""Close local character source/render evidence; never upgrade it to game-pipeline equivalence."""
import hashlib
import json
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
C=ROOT/'artifacts/StaticShaderSamples/HSR-4.6/character-preview-source'
PROJECT=Path('D:/unity-extraction-validation')


def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()


def main():
    plan=json.loads((C/'character-plan.json').read_text())
    build=json.loads((C/'character-unity.json').read_text(encoding='utf-8-sig'))
    native=json.loads((C/'native-motion-validation-resolved.json').read_text())
    auto=json.loads((C/'native-auto-loop.json').read_text())
    if not build['visibleMotion'] or not build['savedReloadSameImage'] or native['status']!='native_smr_motion_observed':
        raise ValueError('Character runtime/roundtrip gates are not closed')
    if build['sourcePlanSha256']!=sha(C/'character-plan.json'):raise ValueError('Stale Unity build report')
    if auto['status']!='native_smr_motion_observed' or not auto['autoLoop'] or not auto['loopWrapObserved']:raise ValueError('Automatic editor preview has not passed a full loop')
    checks=[]
    for cpu,gpu in zip(build['frames'],native['frames'],strict=True):
        if cpu['time']!=gpu['time'] or sha(Path(cpu['image']))!=sha(Path(gpu['image'])) or sha(Path(gpu['image']))!=gpu['sha256']:
            raise ValueError('Native GPU image differs from CPU pose/control or recorded hash')
        checks.append(dict(time=cpu['time'],nativeImage=gpu['image'],sha256=gpu['sha256'],sameAsCpuPose=True))
    for record in build['ownedAssets']:
        if sha(PROJECT/record['path'])!=record['sha256']:raise ValueError('Owned output changed: '+record['path'])
    evidence=[C/'character-plan.json',C/'character-unity.json',C/'native-motion-validation-resolved.json',C/'native-auto-loop.json',C/'body-idle-audit/animation-layout-audit.json']
    ledger=dict(schema='my-hook.static-character-evidence.v1',version='HSR-4.6.0-versionCode118',
        status='limited_character_render_and_native_motion_verified_not_complete_game_reconstruction',
        sourceRoot=plan['sourceRoot'],scene=build['scene'],prefab=build['prefab'],
        confirmed=[dict(claim='Exact source Material PPtrs, source shader programs, native vertex attributes, ordered bone references and rest bind matrices checked.',report=str(C/'character-unity.json')),
            dict(claim='Blendshape float-curve attributes resolve through unique exact CRC32 of original source Mesh channel strings.',mapping=plan['blendShapeAttributeResolution'])],
        observed=[dict(claim='Native SkinnedMeshRenderer rendering at four sampled times equals independently CPU-baked current-pose rendering byte for byte.',checks=checks),
            dict(claim='Automatic editor-time clip sampling changes native rendered frames without manual Sample calls.',report=str(C/'native-auto-loop.json')),
            dict(claim='Repeated time 1 and saved-scene reload are deterministic.',sha256=build['deterministicRepeatSha256'])],
        hypothesis=[],
        blocked=[
            'Original main deferred/GBuffer pipeline, outlines, stencil, eye-shadow composition, shadows and postprocessing are not restored.',
            'Scene lighting/height tint/additive color are explicit neutral test fixtures, not captured game state.',
            'Inline sampler raw word84 uses an explicit PointClamp preview override; meaning not decoded.',
            'Native Renderer rootBone and custom trailing fields are not decoded; no guessed attachment is used.',
            'Animator root/motion curves are not applied; this is direct clip pose preview, not original state-machine/root-motion playback.',
            'AnimationClip AC7 schema retains an unclassified four-byte footer; not a complete TypeTree claim.',
            'Runtime weapon visibility/skill switching and sourceFile#PathID to runtime-object to RenderDoc EID identity are not established.'],
        unsupportedCurves=build['unsupportedNonTransformCurves'],
        files=[dict(file=str(p),sha256=sha(p)) for p in evidence])
    target=C/'evidence-ledger-character.json';target.write_text(json.dumps(ledger,indent=2),encoding='utf-8')
    print(json.dumps(dict(ledger=str(target),frames=len(checks),ownedFiles=len(build['ownedAssets']))))


if __name__=='__main__':main()
