import struct
import unittest

from reconstruct_rdc_sample import freeze_block, verify_decoded_block


class CapturedConstantEvidenceTests(unittest.TestCase):
    def test_vector_component_and_array_stride(self):
        body = "float value : packoffset(c1.y);\nfloat2 pairs[2] : packoffset(c2);"
        data = bytearray(64)
        struct.pack_into("<f", data, 20, 1.5)
        struct.pack_into("<2f", data, 32, 2, 3)
        struct.pack_into("<2f", data, 48, 4, 5)
        variables = [{"floatValues": [1.5]}, {"members": [
            {"floatValues": [2, 3]}, {"floatValues": [4, 5]}]}]
        self.assertEqual(verify_decoded_block(body, data, variables), 5)
        self.assertIn("static const float2 pairs[2]", freeze_block(body, data))

    def test_stale_dynamic_offset_values_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "dynamic offsets"):
            verify_decoded_block("uint flag : packoffset(c0);", struct.pack("<I", 1),
                                 [{"uintValues": [0]}])

    def test_unsupported_member_layout_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "Unsupported"):
            freeze_block("float4x4 matrix : packoffset(c0);", bytes(64))


if __name__ == "__main__":
    unittest.main()
