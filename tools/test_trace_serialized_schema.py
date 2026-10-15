import math
import struct
import unittest

from trace_serialized_schema import TraceReader, SchemaError, PolicyError, BoundaryError


def node(kind, name="value", size=4, children=(), flags=0):
    return dict(type=kind, name=name, byteSize=size, index=0, version=1,
                children=list(children), flags=flags, metaFlags=0, level=0)


class TraceTests(unittest.TestCase):
    def test_width_conflict_rejects_before_read(self):
        reader=TraceReader(b"\0"*8)
        with self.assertRaises(SchemaError):
            reader.read(node("float",size=8))
        self.assertEqual(reader.position,0)

    def test_char_is_unsigned(self):
        self.assertEqual(TraceReader(b"\xff").read(node("char",size=1)),255)

    def test_big_endian_is_carried(self):
        self.assertEqual(TraceReader(struct.pack(">i",12345),endian=">").read(node("int")),12345)

    def test_array_bounds_reject_float_bits_as_count(self):
        reader=TraceReader(struct.pack("<f",1))
        with self.assertRaises(PolicyError):
            reader.read(node("Array",children=[node("int","size"),node("float","data")],flags=1))
        self.assertEqual(reader.position,4)

    def test_array_count_type_must_be_signed_int(self):
        with self.assertRaises(SchemaError):
            TraceReader(b"\0"*4).read(node("Array",children=[node("UInt32","size"),node("float","data")]))

    def test_slope_infinity_only(self):
        raw=struct.pack("<f",math.inf)
        reader=TraceReader(raw)
        reader.read(node("float","inSlope"))
        self.assertEqual(reader.trace[0]["value"],"Infinity")
        with self.assertRaises(PolicyError):
            TraceReader(raw).read(node("float","startSize"))

    def test_missing_bytes_never_resync(self):
        reader=TraceReader(b"\0\0")
        with self.assertRaises(BoundaryError):
            reader.read(node("float"))
        self.assertEqual(reader.position,0)


if __name__=="__main__":
    unittest.main()
