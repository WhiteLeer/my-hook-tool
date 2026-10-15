"""Small CPU-only regression gates; no game execution or GPU replay."""

import copy
import json
import unittest
from pathlib import Path
from types import SimpleNamespace

from extract_apk_shader_program import Reader, decode, parse_platforms, verify_metadata
from build_static_shader_sample import adapt, keyword_names
from unity_mcp_call import require_success


ROOT = Path(__file__).resolve().parents[1] / "artifacts/StaticShaderSamples/HSR-4.6"


class BoundedReaderTests(unittest.TestCase):
    def test_mcp_operation_failure_is_not_a_success_exit(self):
        result = SimpleNamespace(model_dump=lambda: {"content": [{"type": "text", "text": '{"success": false, "error": "wrong instance"}'}]})
        with self.assertRaisesRegex(RuntimeError, "wrong instance"):
            require_success(result)

    def test_mcp_protocol_error_is_not_a_success_exit(self):
        with self.assertRaisesRegex(RuntimeError, "tool returned an error"):
            require_success(SimpleNamespace(model_dump=lambda: {"isError": True, "content": []}))

    def test_rejects_read_past_object(self):
        with self.assertRaises(ValueError):
            Reader(b"1234", 0, 3).take(4)

    def test_rejects_large_array(self):
        with self.assertRaises(ValueError):
            Reader(b"\xff\xff\xff\x7f", 0, 4).array("hostile")

    def test_rejects_unknown_keyword(self):
        with self.assertRaises(ValueError):
            keyword_names({"keywordIndices": {"combined": [99], "global": [], "local": []}}, {})

    def test_rejects_vector_type_mismatch(self):
        source = "cbuffer B : register(b0) { float4 used : packoffset(c0); }; float4 f() { return used; }"
        reflection = {"ubos": [{"name": "B", "set": 1, "binding": 0, "block_size": 16}]}
        parameters = {"constantBuffers": [{"nameIndex": 1, "size": 16,
                       "vectors": [{"nameIndex": 2, "index": 0, "type": 1, "dimension": 4, "arraySize": 0}],
                       "matrices": []}], "constantBufferBindings": [{"nameIndex": 1, "index": 0x04010000}]}
        with self.assertRaisesRegex(ValueError, "type/shape"):
            adapt(source, reflection, parameters, {"B": 1, "_Value": 2}, {"_Value": {"type": "Vector"}}, "vert", [])

    def test_rejects_duplicate_descriptor(self):
        block = {"nameIndex": 1, "size": 16, "vectors": [], "matrices": []}
        parameters = {"constantBuffers": [block, copy.deepcopy(block)],
                      "constantBufferBindings": [{"nameIndex": 1, "index": 0x04010000}]}
        with self.assertRaisesRegex(ValueError, "Duplicate descriptor"):
            adapt("", {"ubos": []}, parameters, {}, {}, "vert", [])


@unittest.skipUnless((ROOT / "apk-shader-material-metadata/shader-report.json").exists(),
                     "Local proprietary fixture is not distributed")
class CurrentApkFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = json.loads((ROOT / "apk-shader-material-metadata/shader-report.json").read_text(encoding="utf-8"))
        cls.shader = next(x for x in cls.report["shaders"] if x["name"] == "miHoYo/CRP_UI/UI_Particle_OneChannel")
        cls.data = (ROOT / "metadata-inputs" / cls.shader["source"]["fileName"]).read_bytes()

    def test_exact_platform_and_object_consumption(self):
        records = parse_platforms(self.data, self.shader, True)
        self.assertEqual([x["platform"] for x in records], ["GLES3Plus", "Vulkan"])
        self.assertTrue(all(x["unidentifiedWord"] == 0 for x in records))
        self.assertEqual(len(decode(records[1], 0)), 3411)
        self.assertIn(b"#ifdef VERTEX", decode(records[0], 0))
        self.assertIn(b"#ifdef FRAGMENT", decode(records[0], 0))

    def test_old_record_layout_fails_closed(self):
        with self.assertRaises(ValueError):
            parse_platforms(self.data, self.shader, False)

    def test_truncated_container_fails_closed(self):
        with self.assertRaises(ValueError):
            parse_platforms(self.data[:-4], self.shader, True)

    def test_extra_object_bytes_fail_closed(self):
        shader = copy.deepcopy(self.shader)
        shader["compiledData"]["objectEnd"] += 4
        with self.assertRaises(ValueError):
            parse_platforms(self.data + b"\x00" * 4, shader, True)

    def test_unmatched_descriptor_rejected(self):
        source = "cbuffer bad : register(b1) { float4 used : packoffset(c0); }; float4 f() { return used; }"
        reflection = {"ubos": [{"name": "bad", "set": 1, "binding": 1, "block_size": 16}]}
        with self.assertRaises(ValueError):
            adapt(source, reflection, {"constantBuffers": [], "constantBufferBindings": []}, {}, {}, "frag", [])

    def test_material_static_references_are_exact(self):
        material = next(x for x in self.report["materials"] if x["name"] == "UI_ChallengePeakBoss_2002_Par")
        self.assertEqual(material["shader"]["sourceFile"], self.shader["source"]["fileName"])
        self.assertEqual(material["shader"]["pathId"], self.shader["pathId"])
        texture = next(x for x in material["textures"] if x["property"] == "_MainTex")
        self.assertEqual(texture["texture"]["sourceFile"], "4e5897dd8c30c6c4aafb4a77187197e4")
        self.assertEqual(texture["texture"]["pathId"], 1)

    def test_metadata_provenance_matches_current_files(self):
        path = ROOT / "apk-shader-material-metadata/shader-report.json"
        provenance = verify_metadata(path, ROOT / "metadata-inputs")
        self.assertEqual(provenance["status"], "fresh_export_inputs_unchanged")


@unittest.skipUnless((ROOT / "UI_Particle_UVMove/metadata/shader-report.json").exists(), "Local UV fixture is not distributed")
class UvMoveFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = json.loads((ROOT / "UI_Particle_UVMove/metadata/shader-report.json").read_text(encoding="utf-8"))
        cls.shader = next(x for x in cls.report["shaders"] if x["name"] == "miHoYo/CRP_UI/UI_Particle_UVMove")
        cls.shader_pass = cls.shader["serialized"]["subShaders"][0]["passes"][0]

    def test_two_variants_have_distinct_exact_keywords_and_programs(self):
        for keyword, expected in (("_CL2_RG",0),("_CL2_X",2)):
            selected = [x for x in self.shader_pass["stages"]["vertex"]["subPrograms"]
                        if x["gpuProgramType"] == "SPIRV" and keyword_names(x,self.shader_pass["nameIndices"]) == [keyword]]
            self.assertEqual({x["blobIndex"] for x in selected},{expected})

    def test_uv_record_layout_consumes_object_and_decodes_programs(self):
        data = (ROOT / "UI_Particle_UVMove/inputs" / self.shader["source"]["fileName"]).read_bytes()
        records = parse_platforms(data,self.shader,True)
        vulkan = next(x for x in records if x["platform"] == "Vulkan")
        self.assertNotEqual(decode(vulkan,0),decode(vulkan,2))

    def test_material_absent_mask_is_shader_declared_white(self):
        material = next(x for x in self.report["materials"] if x["name"] == "UI_ChallengePeakFrame_1001_Wenli")
        self.assertFalse(any(x["property"] == "_MaskTex" for x in material["textures"]))
        prop = next(x for x in self.shader["serialized"]["properties"] if x["name"] == "_MaskTex")
        self.assertEqual(prop["defaultTexture"]["name"],"white")

    def test_named_time_adapter_does_not_invent_a_material_property(self):
        source = "cbuffer B : register(b0) { float4 rawTime : packoffset(c0); }; float4 f() { return rawTime; }"
        reflection = {"ubos": [{"name":"B","set":1,"binding":0,"block_size":16}]}
        parameters = {"constantBuffers":[{"nameIndex":1,"size":16,"vectors":[
            {"nameIndex":2,"index":0,"type":0,"dimension":4,"arraySize":0}],"matrices":[]}],
            "constantBufferBindings":[{"nameIndex":1,"index":0x04010000}]}
        result, _, extras = adapt(source,reflection,parameters,{"B":1,"_Time":2},{},"vert",[])
        self.assertIn("_RecoveredTime",result)
        self.assertEqual(extras,{"_Time"})


if __name__ == "__main__":
    unittest.main()
