"""Source-space character regression gates. Local proprietary inputs are not distributed."""
import hashlib
import json
from pathlib import Path
import unittest
import tempfile
import numpy as np
from prepare_character_motion_plan import resolve_blendshape_attributes

ROOT=Path(__file__).resolve().parents[1]
C=ROOT/'artifacts/StaticShaderSamples/HSR-4.6/character-preview-source'


def matrix(t):
    x,y,z,w=t['rotation']
    rotation=np.array([[1-2*y*y-2*z*z,2*x*y-2*z*w,2*x*z+2*y*w],
                       [2*x*y+2*z*w,1-2*x*x-2*z*z,2*y*z-2*x*w],
                       [2*x*z-2*y*w,2*y*z+2*x*w,1-2*x*x-2*y*y]])
    result=np.eye(4);result[:3,:3]=rotation@np.diag(t['scale']);result[:3,3]=t['position']
    return result


@unittest.skipUnless((C/'character-plan.json').exists(),'Local source character fixture unavailable')
class SourceCharacterTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.plan=json.loads((C/'character-plan.json').read_text())
        cls.meshes=json.loads(Path(cls.plan['meshes']).read_text())

    def test_plan_immutable_sources(self):
        for key in ['meshes','sourcePackage','model','clip']:
            with Path(self.plan[key]).open('rb') as f:
                self.assertEqual(hashlib.file_digest(f,'sha256').hexdigest(),self.plan[key+'Sha256'])

    def test_exact_blendshape_hash_resolution(self):
        mapping=self.plan['blendShapeAttributeResolution']
        self.assertEqual(set(mapping),{'839438197','3355963926','2718742116','1476627207'})
        for value in mapping.values():self.assertEqual(value['count'],1)
        with tempfile.TemporaryDirectory() as folder:
            source=Path(folder)/'raw.anim';destination=Path(folder)/'derived.anim'
            source.write_text('    attribute: blendShape.839438197\n    attribute: blendShape.123\n      attribute: 839438197\n')
            result=resolve_blendshape_attributes(source,destination,['Body_blendShape.Elbow_front_120_L'])
            self.assertIn('attribute: blendShape.Body_blendShape.Elbow_front_120_L',destination.read_text())
            self.assertIn('attribute: blendShape.123',destination.read_text())
            self.assertIn('attribute: 839438197',destination.read_text())
            self.assertEqual(result['839438197']['count'],1)

    def test_native_rest_skin_matrices(self):
        transforms={}
        for t in sorted(self.plan['sourceTransforms'],key=lambda x:len(x['path'].split('/'))):
            path=t['path'];local=matrix(t)
            transforms[path]=local if not path else transforms[path.rsplit('/',1)[0] if '/' in path else '']@local
        count=0
        for r in self.plan['renderers']:
            mesh=self.meshes[r['meshSource']]
            if not r['bones']:continue
            renderer_inverse=np.linalg.inv(transforms[r['path']])
            for path,bind in zip(r['bones'],mesh['bindposes']):
                error=np.max(np.abs(renderer_inverse@transforms[path]@np.array(bind).reshape(4,4)-np.eye(4)))
                self.assertLess(error,1e-5,(r['path'],path,error));count+=1
        self.assertGreater(count,400)

    def test_vertex_color_storage_is_not_guessed(self):
        for mesh in self.meshes.values():
            if mesh['colors']:
                self.assertEqual(mesh['colorFormat'],2)
                colors=np.array(mesh['colors'])
                self.assertTrue(np.issubdtype(colors.dtype,np.integer))
                self.assertGreaterEqual(colors.min(),0);self.assertLessEqual(colors.max(),255)
        source=(ROOT/'unity/Editor/StaticCharacterSampleBuilder.cs').read_text()
        self.assertIn('mesh.colors32=',source)
        self.assertIn('mesh.Clear(false)',source)
        self.assertNotIn('CopySerialized(restored,persistent)',source)

    def test_exact_material_slot_identities(self):
        omitted=set()
        for renderer in self.plan['renderers']:
            self.assertEqual(len(renderer['materials']),len(renderer['materialSources']))
            for asset,pointer in zip(renderer['materials'],renderer['materialSources']):
                identity=pointer['sourceFile']+':'+str(pointer['pathId'])
                if asset is None:omitted.add(identity);continue
                p=Path(pointer['sample']);data=json.loads(p.read_text())
                self.assertEqual(hashlib.sha256(p.read_bytes()).hexdigest(),pointer['sampleSha256'])
                self.assertEqual((data['sourceMaterialFile'],data['sourceMaterialPathId']),(pointer['sourceFile'],pointer['pathId']))
                self.assertEqual(data['sourcePassLightMode'],'CustomReflection')
        self.assertEqual(omitted,{'CAB-a62ffe0c3fe223f92914adeee4c8f648:3727209815991833530',
                                 'CAB-b109a864445d54dfe27e7af888d4a106:3082673868023183515'})

    def test_animation_partial_schema_is_not_marked_complete(self):
        audit=json.loads((C/'body-idle-audit/animation-layout-audit.json').read_text())[0]
        self.assertEqual(audit['typeHash'],'AC7ACA4D7AF2D1C1A4498E0FC82DA456')
        self.assertEqual(audit['candidates'][0]['status'],'rejected')
        self.assertEqual(audit['candidates'][1]['status'],'trailing_bytes')
        self.assertTrue(any('unclassified' in x for x in self.plan['sourceSchemaLimitations']))

    @unittest.skipUnless((C/'native-motion-validation-resolved.json').exists(),'Native verification has not run yet')
    def test_native_motion_matches_independent_pose_render(self):
        report=json.loads((C/'native-motion-validation-resolved.json').read_text())
        build=json.loads((C/'character-unity.json').read_text(encoding='utf-8-sig'))
        self.assertEqual(report['status'],'native_smr_motion_observed')
        self.assertTrue(report['noCpuBake']);self.assertTrue(build['savedReloadSameImage'])
        self.assertEqual(len({f['sha256'] for f in report['frames']}),4)
        for cpu,gpu in zip(build['frames'],report['frames'],strict=True):
            self.assertEqual(cpu['time'],gpu['time'])
            self.assertEqual(hashlib.sha256(Path(cpu['image']).read_bytes()).hexdigest(),gpu['sha256'])
            self.assertEqual(hashlib.sha256(Path(gpu['image']).read_bytes()).hexdigest(),gpu['sha256'])
        self.assertEqual(len(build['unsupportedNonTransformCurves']),14)


if __name__=='__main__':unittest.main()
