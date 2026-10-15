import unittest
import struct
from pathlib import Path
from hsr_static_catalog import design_index, AssetCatalog
from prepare_hyacine_skill_probe import phase01, audit_motion

A=Path(__file__).resolve().parents[1]/'artifacts/StaticShaderSamples/HSR-4.6/hyacine-attack-source'


class AssetBoundsTests(unittest.TestCase):
    def catalog(self,offset,size):
        d=bytearray(168);d[:8]=bytes.fromhex('5352414d00050001')
        sections=[(0,168,1,24),(0,192,0,4),(0,192,1,32),(0,224,0,4),(0,224,0,8),(0,224,0,12),(0,224,0,4),(0,224,0,8)]
        for i,r in enumerate(sections):struct.pack_into('<4I',d,40+i*16,*r)
        return d+bytes(16)+struct.pack('<2I',0,5)+struct.pack('<4IQIHH',0,0,offset,size,7,0,0,0)

    def test_out_of_block_bundle_rejected(self):
        with self.assertRaisesRegex(ValueError,'Bundle range'):AssetCatalog(self.catalog(4,5))

    def test_valid_bundle_range(self):
        self.assertEqual(AssetCatalog(self.catalog(0,5)).paths[7]['bundleBytes'],5)


@unittest.skipUnless((A/'skill01-source.json').exists(),'Local proprietary skill fixture unavailable')
class SkillSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import json
        cls.e=json.loads((A/'skill01-source.json').read_text())
        with Path(cls.e['pack']).open('rb') as file:
            file.seek(cls.e['item']['offset']);cls.body=file.read(cls.e['item']['size'])

    def test_current_subset_record_boundaries(self):
        p=phase01(self.body)
        self.assertEqual(len(p['tasks']),10);self.assertEqual(len(p['effects']),5)
        self.assertEqual(p['onStartEnd'],790)
        self.assertEqual([e['offset'] for e in p['effects']],[154,276,398,520,642])
        self.assertEqual([e['targetAlias'] for e in p['effects']],['AbilityTargetEntity']*5)
        self.assertEqual(p['opaqueRemainderBytes'],9569)

    def test_nonfinite_and_negative_delay_rejected(self):
        p=phase01(self.body)
        for value in [float('nan'),float('inf'),-1.0]:
            data=bytearray(self.body);offset=p['effects'][0]['end']-5
            struct.pack_into('<f',data,offset,value)
            with self.assertRaises(ValueError):phase01(data)

    def test_motion_identity_is_controller_derived(self):
        m=audit_motion()
        self.assertEqual(m['clipPathId'],86046338945872209)
        self.assertEqual(m['clipSlot'],24)
        self.assertEqual(m['clipFile'],'CAB-9dd02fbe900993acb86386004af348fc')


if __name__=='__main__':unittest.main()
