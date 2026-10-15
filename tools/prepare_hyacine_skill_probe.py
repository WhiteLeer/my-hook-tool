"""A bounded Skill01 fixture: current binary task records, exact catalog paths and native motion."""
import hashlib
import json
import shutil
import struct
import subprocess
import tempfile
import math
import re
from pathlib import Path

from hsr_static_catalog import AssetCatalog, design_index, verify_content_file
from prepare_character_motion_plan import resolve_blendshape_attributes
from hsr_static_container import load_container

ROOT = Path(__file__).resolve().parents[1]
V = ROOT / 'artifacts/StaticShaderSamples/HSR-4.6'
A = V / 'hyacine-attack-source'
C = V / 'character-preview-source'
REFERENCE = ('https://raw.githubusercontent.com/DimbreathBot/TurnBasedGameData/'
             '02b00aadbf73627920ce07e3254f939008f5fb8e/Config/ConfigAbility/Avatar/Avatar_Hyacine_00_Ability.json')


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


class TaskReader:
    def __init__(self, data, position=0):
        self.data, self.pos = data, position

    def take(self, count):
        if count < 0 or self.pos + count > len(self.data):
            raise ValueError('Task extends beyond catalog item')
        value = self.data[self.pos:self.pos + count]
        self.pos += count
        return value

    def expect(self, value):
        if self.take(len(value)) != value:
            raise ValueError('Unsupported task layout; never skip/scan to the next string')

    def varint(self):
        result = 0
        for shift in range(0, 70, 7):
            value = self.take(1)[0]
            result |= (value & 127) << shift
            if value < 128:
                return result
        raise ValueError('Overlong varint')

    def text(self):
        length = self.varint()
        if length > 4096:
            raise ValueError('String budget exceeded')
        return self.take(length).decode('utf-8')


def phase01(data):
    r = TaskReader(data)
    r.expect(bytes.fromhex('03 12 13'))
    ability = r.text()
    if ability != 'Avatar_Hyacine_00_Skill01_Phase01':
        raise ValueError('Unexpected ability item')
    r.expect(bytes.fromhex('01 10 14'))
    tasks = []
    for _ in range(2):
        start = r.pos
        r.expect(bytes.fromhex('f5 1b 32 0c 01'))
        target = r.text()
        r.expect(b'\x04')
        name = r.text()
        r.expect(b'\x01')
        tasks.append(dict(kind='TriggerAbility', targetAlias=target, value=name, offset=start, end=r.pos))
    start = r.pos
    r.expect(bytes.fromhex('fc 0c 44 04'))
    duration = struct.unpack('<f', r.take(4))[0]
    tasks.append(dict(kind='LookAt', offset=start, end=r.pos, duration=duration, targetRaw=4))
    start = r.pos
    r.expect(bytes.fromhex('fb 1b 08'))
    state = r.text()
    tasks.append(dict(kind='TriggerAnimState', value=state, offset=start, end=r.pos))
    effects = []
    for _ in range(5):
        start = r.pos
        r.expect(bytes.fromhex('96 1c 82 84 88 01 0c 01'))
        alias, path = r.text(), r.text()
        raw = r.take(4)
        delay = struct.unpack('<f', raw)[0]
        if not math.isfinite(delay) or delay<0 or delay>60:raise ValueError('Invalid bounded skill delay')
        r.expect(b'\x00')
        effect = dict(kind='TriggerEffect', targetAlias=alias, sourcePath=path, delay=delay,
                      delayFloat32Hex=raw.hex(), offset=start, end=r.pos, forceSimulateImmediately=False)
        effects.append(effect)
        tasks.append(effect)
    start = r.pos
    r.expect(bytes.fromhex('c2 1d 0e 0c 01'))
    target, state_end = r.text(), r.text()
    r.expect(b'\x00')
    normalized_opaque = r.data[r.pos:r.pos + 5].hex()
    r.varint()
    tasks.append(dict(kind='WaitAnimState', targetAlias=target, value=state_end, offset=start,
                      end=r.pos, normalizedTimeOpaque=normalized_opaque))
    # The next ability starts immediately. It is not executed by this preview.
    end = r.pos
    r.expect(b'\x33')
    if r.text() != 'Avatar_Hyacine_00_Skill01_Phase02':
        raise ValueError('Task boundary does not end at the independently checked next ability')
    return dict(ability=ability, animationState=state, tasks=tasks, effects=effects, onStartEnd=end,
                sourceStatus='current_binary_subset_matches_pinned_old_schema_not_complete_config_decode',
                schemaReference=REFERENCE, schemaReferenceVersion='4.2, field-label reference only; values read from local 4.6 bytes',
                opaqueRemainderBytes=len(data) - end)


def audit_motion():
    wrapper=C/'body-discovery/probe/1b25141f9cee18c437c39ba890485441/containers/CAB-ae4dd3b7d799dffdbef55644f34fe1ee'
    animator=next(o for o in load_container(wrapper).objects if o.type.value==95 and o.path_id==-6946018335855933535)
    raw=animator.get_raw_data()
    file_id,path_id=struct.unpack_from('<iq',raw,28)
    controller_file=Path(animator.assets_file.externals[file_id-1].path).name
    controller_path=wrapper.parent/controller_file
    controller=next(o for o in load_container(controller_path).objects if o.type.value==91 and o.path_id==path_id)
    data=controller.read_typetree(check_read=True)
    labels={key:value for key,value in data['m_TOS']}
    matches=[]
    for machine in data['m_Controller']['m_StateMachineArray']:
        for entry in machine['data']['m_StateConstantArray']:
            state=entry['data']
            if labels.get(state['m_FullPathID'])=='Base Layer.SkillSubs.Skill01':matches.append(state)
    if len(matches)!=1:raise ValueError('Skill state is not unique in controller')
    trees=matches[0]['m_BlendTreeConstantArray']
    if len(trees)!=1 or len(trees[0]['data']['m_NodeArray'])!=1:raise ValueError('Unexpected motion blend; do not guess')
    slot=trees[0]['data']['m_NodeArray'][0]['data']['m_ClipID']
    pointer=data['m_AnimationClips'][slot]
    clip_file=Path(controller.assets_file.externals[pointer['m_FileID']-1].path).name
    source=A/'candidate-motion/containers'/clip_file
    clip=next(o for o in load_container(source).objects if o.type.value==74 and o.path_id==pointer['m_PathID'])
    raw_clip=clip.get_raw_data();name_length=struct.unpack_from('<I',raw_clip)[0]
    if not 0<name_length<4096:raise ValueError('Invalid bounded clip name')
    clip_name=raw_clip[4:4+name_length].decode('utf-8')
    return dict(animatorFile=wrapper.name,animatorPathId=animator.path_id,animatorFileSha256=sha(wrapper),
                controllerFile=controller_file,controllerPathId=path_id,controllerFileSha256=sha(controller_path),
                state='Base Layer.SkillSubs.Skill01',clipSlot=slot,clipFile=clip_file,clipPathId=clip.path_id,
                clipName=clip_name,
                clipObjectSha256=hashlib.sha256(clip.get_raw_data()).hexdigest(),clipFileSha256=sha(source))


def main():
    d = A / 'config-index'
    index = d / 'DesignV_702d7f1e6dc48d92b85f9ae4ca2b139b.bytes'
    files = design_index(index.read_bytes())
    file = next(x for x in files if x['md5'] == '4360a7818923768004d703df2b15eecd')
    pack = d / (file['md5'] + '.bytes')
    verify_content_file(pack, file['md5'], file['size'])
    item = next(x for x in file['items'] if x['key'] == 'dec7d294451fc6d0')
    with pack.open('rb') as stream:
        stream.seek(item['offset'])
        body = stream.read(item['size'])
    evidence = phase01(body)
    evidence['motionIdentity']=audit_motion()
    for task in evidence['tasks']:
        task['absoluteOffset']=item['offset']+task['offset'];task['absoluteEnd']=item['offset']+task['end']
    evidence.update(designIndex=str(index), designIndexSha256=sha(index), pack=str(pack), packMd5=file['md5'],
                    item=item, itemSha256=hashlib.sha256(body).hexdigest(),
                    fullConfigPathStatus='not_proven_from_item_key_do_not_assert_full_json_path')
    catalog_path = d / 'AsbV_8618878d465b2985fb60d5ef74e9a5ad.bytes'
    verify_content_file(catalog_path, '8618878d465b2985fb60d5ef74e9a5ad', 81680036)
    catalog = AssetCatalog(catalog_path.read_bytes())
    directory_path = V / 'battle-effect-source/directory-index/cab-directory.json'
    directory = json.loads(directory_path.read_text())['records']
    for effect in evidence['effects']:
        effect['resource'] = catalog.lookup(effect['sourcePath'], directory)
    evidence.update(assetCatalog=str(catalog_path), assetCatalogSha256=sha(catalog_path),
                    cabDirectory=str(directory_path), cabDirectorySha256=sha(directory_path))
    (A / 'skill01-source.json').write_text(json.dumps(evidence, indent=2), encoding='utf-8')

    inputs = A / 'motion-inputs'
    inputs.mkdir(exist_ok=True)
    for p in (C / 'hyacine-closure/inputs').iterdir():
        if p.is_file():
            shutil.copyfile(p, inputs / p.name)
    motion=evidence['motionIdentity']
    source = A / 'candidate-motion/containers'/motion['clipFile']
    shutil.copyfile(source, inputs / source.name)
    cli = ROOT.parents[1] / 'Base_AS/AnimeStudio-master/AnimeStudio.CLI/bin/Release/net8.0-windows/AnimeStudio.CLI.exe'
    with tempfile.TemporaryDirectory(prefix='fresh-motion-',dir=A) as temporary:
        command = [str(cli), str(inputs), temporary, '--game', 'SR', '--batch_load',
               '--export_type', 'FBX', '--group_assets', 'ByModel', '--types', 'GameObject:Both', 'AnimationClip:Both',
               '--names', '^(Art_Hyacine_01|'+re.escape(motion['clipName'])+')$', '--logger_flags', 'Info', 'Warning', 'Error']
        run = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', timeout=90)
        (A / 'motion-export.log').write_text(run.stdout + run.stderr, encoding='utf-8')
        if run.returncode or '[Error]' in run.stdout:raise ValueError('Inspect animation export errors')
        clips = list(Path(temporary).rglob(motion['clipName']+'.anim'))
        if len(clips)!=1:raise ValueError('Fresh export did not resolve uniquely')
        folder=A/'original-motion';folder.mkdir(exist_ok=True)
        original=folder/clips[0].name;shutil.copyfile(clips[0],original)
    clips=[original]
    native = A / 'native-motion'
    native.mkdir(exist_ok=True)
    mesh_data = json.loads((C / 'character-meshes.json').read_text())
    derived = native / clips[0].name
    mapping = resolve_blendshape_attributes(clips[0], derived, {s['name'] for m in mesh_data.values() for s in m['shapes']})
    plan = dict(schema='my-hook.skill-preview.v1', characterPrefab='Assets/RenderDocShaderSamples/HSR-4.6/HyacineCharacter/_Preview/CharacterPreview.prefab',
                clip=str(derived), clipSha256=sha(derived), originalClip=str(clips[0]), originalClipSha256=sha(clips[0]),
                blendShapeAttributeResolution=mapping, sourceClipFile=source.name, sourceClipPathId=motion['clipPathId'],
                sourceEvidence=str(A / 'skill01-source.json'), sourceEvidenceSha256=sha(A / 'skill01-source.json'),
                scope='Original Skill01 clip on the verified native rig. Effect schedule is a validated binary subset; not full combat logic or recovered game Timeline.',
                effects=[], unresolvedEffects=evidence['effects'], previewTimes=[0, 0.2, 0.6, 1.1, 1.7, 2.3],
                targetFixture=dict(position=[0, 0, 2], note='Explicit stationary skill target fixture, not recovered battle placement'),
                limitations=['Original phase02 gameplay, camera, environment and full Animator transitions are not reconstructed.',
                             'The character still uses the earlier isolated CustomReflection-pass fixture, not the main game pipeline.'])
    (A / 'skill-plan.json').write_text(json.dumps(plan, indent=2), encoding='utf-8')
    print(json.dumps(dict(sourceEffects=len(evidence['effects']), clip=str(derived), plan=str(A / 'skill-plan.json'))))


if __name__ == '__main__':
    main()
