"""CPU regression gates for the real gameplay sample; proprietary fixtures stay external."""
import hashlib
import json
import struct
import unittest
from pathlib import Path
from unittest.mock import patch
from hsr_static_container import load_container
from UnityPy.files.SerializedFile import SerializedFile
from UnityPy.helpers.UnityVersion import UnityVersion
from extract_apk_shader_program import verify_metadata
from build_static_shader_sample import adapt, keyword_names
from read_guest_bundle_directory import decode

ROOT=Path(__file__).resolve().parents[1]
B=ROOT/'artifacts/StaticShaderSamples/HSR-4.6/battle-effect-source'


class TransportTests(unittest.TestCase):
    def test_binary_footer(self):
        self.assertEqual(decode(b'BHD1'+struct.pack('<HIIIQ',0,0,0,0,0)),([],[]))

    def test_truncated_footer_rejected(self):
        with self.assertRaises((ValueError,struct.error)):
            decode(b'BHD1\0\0\0')

    def test_invalid_signature_rejected(self):
        with self.assertRaises(ValueError): decode(b'BAD!')


class VersionAdapterTests(unittest.TestCase):
    def test_known_suffix_preserved_and_parser_restored(self):
        original=SerializedFile.set_version
        class Target: pass
        obj=Target()
        def fake_load(path):
            SerializedFile.set_version(obj,'2019.4.34f1\n2')
            return obj
        with patch('hsr_static_container.UnityPy.load',side_effect=fake_load):
            self.assertIs(load_container('unused'),obj)
        self.assertEqual(obj.unity_version,'2019.4.34f1\n2')
        self.assertEqual(obj.version,UnityVersion.from_str('2019.4.34f1'))
        self.assertIs(SerializedFile.set_version,original)

    def test_unknown_suffix_delegates_instead_of_stripping(self):
        with patch.object(SerializedFile,'set_version') as original:
            def fake_load(path): SerializedFile.set_version(object(),'2019.4.34f1\n3')
            with patch('hsr_static_container.UnityPy.load',side_effect=fake_load):
                load_container('unused')
            self.assertEqual(original.call_args.args[1],'2019.4.34f1\n3')


@unittest.skipUnless((B/'hit-comparison/comparison.json').exists(),'Local gameplay fixture is not distributed')
class BattleFixtureTests(unittest.TestCase):
    def test_source_comparison_consumption_and_owners(self):
        r=json.loads((B/'hit-comparison/comparison.json').read_text())
        self.assertTrue(r['passed'])
        self.assertEqual(len(r['components']),4)
        self.assertEqual(sum(c['comparedValues'] for c in r['components']),4939)
        for c in r['components']:
            self.assertEqual(c['mismatches'],[])
            self.assertTrue(c['ownerReverseComponentVerified'])

    def test_exact_keyword_pair_is_non_ui_shader(self):
        report=B/'hit-metadata/shader-report.json'
        verify_metadata(report,B/'hit-metadata-inputs')
        r=json.loads(report.read_text(encoding='utf-8-sig'))
        shader=r['shaders'][0]
        self.assertEqual(shader['name'],'miHoYo/CRP_Particles/Particle_OneChannel')
        p=shader['serialized']['subShaders'][0]['passes'][0]
        for stage,index in [('vertex',1),('fragment',129)]:
            selected=[x for x in p['stages'][stage]['subPrograms'] if x['gpuProgramType']=='SPIRV'
                      and keyword_names(x,p['nameIndices'])==['_SoftParticle']]
            self.assertEqual({x['blobIndex'] for x in selected},{index})

    def test_external_texture_resource_provenance(self):
        p=json.loads((B/'hit-metadata/metadata-provenance.json').read_text())
        self.assertEqual(len(p['resources']),1)
        for name,h in p['resources'].items():
            self.assertEqual(hashlib.sha256((B/'hit-metadata-inputs'/name).read_bytes()).hexdigest(),h)

    def test_native_source_capacity_and_burst_are_not_inflated(self):
        p=json.loads((B/'hit-plan.json').read_text())
        systems=[c['data'] for n in p['nodes'] for c in n['components'] if c['type']=='ParticleSystem']
        self.assertEqual([s['InitialModule']['maxNumParticles'] for s in systems],[0,2])
        hit=systems[1]
        self.assertAlmostEqual(hit['InitialModule']['startLifetime']['scalar'],0.3)
        self.assertEqual(hit['EmissionModule']['m_Bursts'][0]['countCurve']['scalar'],5)
        self.assertEqual(p['displayMode'],'native')

    def test_actual_glsl_hlsl_are_not_ui_replacements(self):
        s=json.loads((B/'hit-sample/sample.json').read_text())
        self.assertEqual(s['sourceMaterialPathId'],-7211189983586654392)
        self.assertEqual(s['sourceShaderPathId'],-7177827563780441733)
        self.assertEqual(s['environmentContract']['status'],'preview_fixture_not_recovered_game_state')
        self.assertEqual(len(s['bindingEvidence']),39)
        self.assertFalse(any(x in s['sourceShaderName'] for x in ['CRP_UI','Fallback']))


if __name__=='__main__': unittest.main()
