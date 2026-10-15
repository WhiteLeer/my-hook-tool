import struct
import unittest
from hsr_static_catalog import design_index, verify_content_file
from tempfile import TemporaryDirectory
from pathlib import Path


class CatalogTests(unittest.TestCase):
    def data(self):
        return (struct.pack('>4I', 255, 4, 1, 2) + struct.pack('>Q16sQI', 13, bytes(16), 5, 2)
                + struct.pack('>QIIQII', 21, 2, 0, 22, 3, 2) + b'\x00\x02th\x80')

    def test_variable_record_tail(self):
        result = design_index(self.data())
        self.assertEqual(result[0]['text'], 'th')
        self.assertEqual(result[0]['items'][1]['offset'], 2)

    def test_truncation_and_tail_rejected(self):
        for data in [self.data()[:-1], self.data() + b'\x00', self.data()[:-1] + b'\x00']:
            with self.assertRaises(ValueError):
                design_index(data)

    def test_noncontiguous_range_rejected(self):
        data = bytearray(self.data())
        struct.pack_into('>I', data, 16 + 36 + 16 + 12, 1)
        with self.assertRaises(ValueError):
            design_index(data)

    def test_file_hash_is_not_filename_trust(self):
        with TemporaryDirectory() as temp:
            file = Path(temp) / 'a.bytes'
            file.write_bytes(b'abc')
            verify_content_file(file, '900150983cd24fb0d6963f7d28e17f72', 3)
            with self.assertRaises(ValueError):
                verify_content_file(file, '0' * 32, 3)


if __name__ == '__main__':
    unittest.main()
