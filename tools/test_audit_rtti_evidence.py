import struct
import unittest
from audit_rtti_evidence import audit


class EvidenceAuditTests(unittest.TestCase):
    def fixture(self):
        library = bytearray(1024)
        library[:6] = b"\x7fELF\x02\x01"
        struct.pack_into("<H", library, 18, 183)
        struct.pack_into("<Q", library, 32, 64)
        struct.pack_into("<HH", library, 54, 56, 2)
        struct.pack_into("<IIQQQQQQ", library, 64, 1, 5, 512, 0x1000, 0, 512, 512, 4096)
        struct.pack_into("<IIQQQQQQ", library, 120, 4, 4, 192, 192, 0, 20, 20, 4)
        struct.pack_into("<III", library, 192, 4, 4, 3)
        library[204:212] = b"GNU\0\x01\x02\x03\x04"
        library[512:516] = b"test"
        raw = bytearray(80)
        struct.pack_into("<Q", raw, 8, 0x101000)
        struct.pack_into("<ii", raw, 40, 21, 264)
        return bytes(library), dict(kind="readonly-rtti-code-evidence", rawHex=raw.hex(), rawReadable=True,
            structureBytes=80, factoryPointerOffset=8, typeIdOffset=40, sizeOffset=44, selectedTypeId=21,
            candidateFactoryAddress="0x101000", loadedCodeHex="74657374", diskCodeHex="74657374",
            codeBytes=4, candidateFileOffset=512, loadedCodeReadable=True, diskCodeReadable=True,
            codeMappingBegin="0x101000", codeMappingEnd="0x101200", codeMappingFileOffset=512,
            captureSessionId="test-session", processId=10, threadId=11, monotonicTimeNs=2, eventSequence=4,
            runtimeArchitecture="x86_64", backingFileBuildId="01020304",
            rttiAddress="0x202000", rttiMappingBegin="0x202000", rttiMappingEnd="0x202100", rttiMappingPermissions="rw-p",
            rttiMappingPath="/data/app/com.example-abc/lib/arm64/libunity.so",
            codeMappingPath="/data/app/com.example-abc/lib/arm64/libunity.so",
            rawVmReadBytes=-1, rawVmReadErrno=22, rawMemReadBytes=80, rawMemReadErrno=0,
            codeVmReadBytes=-1, codeVmReadErrno=22, codeMemReadBytes=4, codeMemReadErrno=0)

    def check(self, event, library):
        profile = dict(androidPackage="com.example", targetLibraryBuildId="01020304",
            readOnlyRttiLayout=dict(structureBytes=80, factoryPointerOffset=8, typeIdOffset=40, sizeOffset=44))
        module = dict(captureSessionId="test-session", processId=10, eventSequence=3, monotonicTimeNs=1,
            mappedPath="/data/app/com.example-abc/lib/arm64/libunity.so", backingFileBuildId="01020304",
            loadedElfMachine=183, headersEqual=True, diskHeaderHex=library[:64].hex(),
            loadedHeaderHex=library[:64].hex(), observerArchitecture="x86_64")
        return audit(event, library, profile, module, "test-session", {21: 264})

    def test_exact_bytes_do_not_validate_abi(self):
        library, event = self.fixture()
        result = self.check(event, library)
        self.assertEqual(result["candidateElfVaddr"], "0x1000")
        self.assertFalse(result["abiValidated"])
        self.assertFalse(result["identityBridgeEstablished"])

    def test_reject_stale_runtime_code(self):
        library, event = self.fixture()
        event["loadedCodeHex"] = "00000000"
        with self.assertRaisesRegex(ValueError, "bytes differ"):
            self.check(event, library)

    def test_reject_wrong_layout_id(self):
        library, event = self.fixture()
        event["typeIdOffset"] = 44
        with self.assertRaisesRegex(ValueError, "layout contract"):
            self.check(event, library)

    def test_reject_nonexecuting_segment(self):
        library, event = self.fixture()
        library = bytearray(library)
        struct.pack_into("<I", library, 68, 4)
        with self.assertRaisesRegex(ValueError, "executable"):
            self.check(event, library)

    def test_reject_missing_recorded_bytes(self):
        library, event = self.fixture()
        event["diskCodeHex"] = ""
        with self.assertRaisesRegex(ValueError, "Incomplete"):
            self.check(event, library)

    def test_reject_wrong_process(self):
        library, event = self.fixture()
        event["processId"] = 99
        with self.assertRaisesRegex(ValueError, "Process provenance"):
            self.check(event, library)

    def test_reject_wrong_session(self):
        library, event = self.fixture()
        event["captureSessionId"] = "stale-session"
        with self.assertRaisesRegex(ValueError, "session mismatch"):
            self.check(event, library)

    def test_reject_wrong_module_path(self):
        library, event = self.fixture()
        event["codeMappingPath"] = "/tmp/libunity.so"
        with self.assertRaisesRegex(ValueError, "path provenance"):
            self.check(event, library)

    def test_reject_wrong_build_id(self):
        library, event = self.fixture()
        event["backingFileBuildId"] = "bad"
        with self.assertRaisesRegex(ValueError, "Build ID"):
            self.check(event, library)

    def test_reject_outside_rtti_mapping(self):
        library, event = self.fixture()
        event["rttiAddress"] = "0x303000"
        with self.assertRaisesRegex(ValueError, "RTTI window outside"):
            self.check(event, library)

    def test_reject_short_structure_contract(self):
        library, event = self.fixture()
        event["rawHex"] = event["rawHex"][:96]
        event["structureBytes"] = 48
        with self.assertRaisesRegex(ValueError, "layout contract"):
            self.check(event, library)

    def test_reject_aliased_size_offset(self):
        library, event = self.fixture()
        event["sizeOffset"] = 40
        with self.assertRaisesRegex(ValueError, "layout contract"):
            self.check(event, library)

    def test_reject_counter_contradiction(self):
        library, event = self.fixture()
        event["rawMemReadBytes"] = 0
        event["rawMemReadErrno"] = 5
        with self.assertRaisesRegex(ValueError, "successful read channel"):
            self.check(event, library)

    def test_preserve_failed_channel_diagnostic(self):
        library, event = self.fixture()
        result = self.check(event, library)
        self.assertEqual(result["rawReadChannel"]["vmErrno"], 22)
        self.assertEqual(result["codeReadChannel"]["method"], "proc_self_mem")

    def test_reject_size_catalog_disagreement(self):
        library, event = self.fixture()
        raw = bytearray.fromhex(event["rawHex"])
        struct.pack_into("<i", raw, 44, 21)
        event["rawHex"] = raw.hex()
        with self.assertRaisesRegex(ValueError, "catalog row"):
            self.check(event, library)


if __name__ == "__main__":
    unittest.main()
