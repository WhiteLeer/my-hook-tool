import json
import struct
import tempfile
import unittest
import zipfile
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from audit_static_prefab import audit
from extract_apk_dependency_closure import extract,serialized_header


class PrefabPreflightTests(unittest.TestCase):
    def package(self, root, component, fields=None):
        path=root/"sample.srprefab"
        manifest={"Name":"test","SourceCAB":"test-source","RootPathID":1,"Materials":[{}],
                  "Nodes":[{"Path":"root","Components":[component]}],"UnsupportedComponents":[]}
        with zipfile.ZipFile(path,"w") as archive:
            archive.writestr("manifest.json",json.dumps(manifest))
            if fields is not None:
                archive.writestr("parameters.json",json.dumps(fields))
        return path

    def test_opaque_tail_does_not_count_as_complete_decode(self):
        with tempfile.TemporaryDirectory() as directory:
            component={"Type":"ParticleSystem","PathID":2,"ParametersStatus":"sr44-particle-partial",
                       "ParametersFile":"parameters.json","ObjectByteSize":100,"ParsedBytes":0,"RemainingBytes":0}
            result=audit(self.package(Path(directory),component,{"UnmappedTailBytes":84}))
            self.assertFalse(result["unityImportAllowed"])
            self.assertEqual(result["components"][0]["rawTailBytes"],84)
            self.assertFalse(result["structuralGatePassed"])

    def test_exact_byte_count_does_not_prove_current_schema(self):
        with tempfile.TemporaryDirectory() as directory:
            component={"Type":"ParticleSystem","PathID":2,"ParametersStatus":"external-type-tree-exported",
                       "ObjectByteSize":100,"ParsedBytes":100,"RemainingBytes":0,"ParametersFile":"parameters.json"}
            result=audit(self.package(Path(directory),component,{"lengthInSec":5}))
            self.assertTrue(result["structuralGatePassed"])
            self.assertFalse(result["currentVersionSchemaVerified"])
            self.assertFalse(result["unityImportAllowed"])

    def test_overread_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            component={"Type":"ParticleSystem","PathID":2,"ParametersStatus":"type-tree-error",
                       "ObjectByteSize":100,"ParsedBytes":120,"RemainingBytes":-20}
            self.assertFalse(audit(self.package(Path(directory),component))["structuralGatePassed"])

    def test_tail_alone_blocks_an_otherwise_complete_component(self):
        with tempfile.TemporaryDirectory() as directory:
            component={"Type":"ParticleSystem","PathID":2,"ParametersStatus":"external-type-tree-exported",
                       "ParametersFile":"parameters.json","ObjectByteSize":100,"ParsedBytes":100,"RemainingBytes":0}
            result=audit(self.package(Path(directory),component,{"UnmappedTailHex":"ABCD"}))
            self.assertFalse(result["structuralGatePassed"])
            self.assertEqual(result["components"][0]["rawTailBytes"],2)

    def test_zero_count_cannot_hide_hex_tail(self):
        with tempfile.TemporaryDirectory() as directory:
            component={"Type":"ParticleSystem","PathID":2,"ParametersStatus":"external-type-tree-exported",
                       "ParametersFile":"parameters.json","ObjectByteSize":100,"ParsedBytes":100,"RemainingBytes":0}
            result=audit(self.package(Path(directory),component,{"UnmappedTailBytes":0,"UnmappedTailHex":"AB"}))
            self.assertFalse(result["structuralGatePassed"])
            self.assertIn("opaque_tail_fields_disagree",[x["reason"] for x in result["errors"]])

    def test_non_particle_partial_is_also_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            component={"Type":"Animator","PathID":2,"ParametersStatus":"external-type-tree-partial"}
            self.assertFalse(audit(self.package(Path(directory),component))["structuralGatePassed"])


class SplitClosureTests(unittest.TestCase):
    def test_split_pieces_are_assembled_by_numeric_index(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            apk=root/"test.apk"
            with zipfile.ZipFile(apk,"w") as archive:
                archive.writestr("assets/bin/Data/test.split1",b"abcd")
                archive.writestr("assets/bin/Data/test.split0",struct.pack(">4I",1,24,21,20)+b"\0"*4)
            with patch("extract_apk_dependency_closure.UnityPy.load",return_value=SimpleNamespace(assets=[SimpleNamespace(externals=[])])):
                extract(apk,"test",root/"output")
            self.assertEqual((root/"output/inputs/test").read_bytes(),struct.pack(">4I",1,24,21,20)+b"\0"*4+b"abcd")

    def test_missing_final_split_is_detected_by_declared_size(self):
        with self.assertRaisesRegex(ValueError,"split suffix"):
            serialized_header(struct.pack(">4I",1,24,21,20)+b"\0"*4)

    def test_format22_uses_extended_size(self):
        data=struct.pack(">4I",0,0,22,0)+b"\0"*4+struct.pack(">IQQQ",1,52,48,0)+b"abcd"
        self.assertEqual(serialized_header(data),22)
        with self.assertRaises(ValueError):
            serialized_header(data[:-1])

    def test_existing_output_is_not_reused(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            output=root/"output"
            output.mkdir()
            (output/"closure.json").write_text("old")
            with self.assertRaisesRegex(ValueError,"new or empty"):
                extract(root/"not-read.apk","test",output)

    def test_missing_split_index_is_not_silently_assembled(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            apk=root/"test.apk"
            with zipfile.ZipFile(apk,"w") as archive:
                archive.writestr("assets/bin/Data/test.split0",b"a")
                archive.writestr("assets/bin/Data/test.split2",b"b")
            extract(apk,"test",root/"output")
            report=json.loads((root/"output/closure.json").read_text())
            self.assertEqual(report["status"],"missing_external_files")
            self.assertFalse((root/"output/inputs/test").exists())

    def test_split_total_respects_byte_budget(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            apk=root/"test.apk"
            with zipfile.ZipFile(apk,"w") as archive:
                archive.writestr("assets/bin/Data/test.split0",b"123")
                archive.writestr("assets/bin/Data/test.split1",b"456")
            with self.assertRaisesRegex(ValueError,"budget"):
                extract(apk,"test",root/"output",max_bytes=5)

    def test_duplicate_split_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            apk=root/"test.apk"
            with zipfile.ZipFile(apk,"w") as archive:
                archive.writestr("assets/bin/Data/test.split0",b"a")
                archive.writestr("assets/bin/Data/test.split0",b"b")
            with self.assertRaisesRegex(ValueError,"duplicate split"):
                extract(apk,"test",root/"output")


if __name__=="__main__":
    unittest.main()
