import unittest
from compare_prefab_schema_trace import checked_flatten, validate_as_coverage


class ComparisonTests(unittest.TestCase):
    def test_collision_rejected(self):
        with self.assertRaisesRegex(ValueError,'Duplicate'):
            checked_flatten({'a.b':1,'a':{'b':2}})

    def test_exact_coverage_and_error_required(self):
        c=dict(ParametersStatus='external-type-tree-exported',ParametersError='',RemainingBytes=0,ParsedBytes=10,ObjectByteSize=10)
        self.assertTrue(validate_as_coverage(c,10))
        for key,value in [('ParsedBytes',8),('ObjectByteSize',11),('ParametersError','partial'),('RemainingBytes',2)]:
            self.assertFalse(validate_as_coverage(dict(c,**{key:value}),10))

    def test_array_elements_and_count_are_compared(self):
        self.assertEqual(checked_flatten({'keys':[{'time':0},{'time':1}]}),
            {'Base.keys.Array.size':2,'Base.keys.Array[0].data.time':0,'Base.keys.Array[1].data.time':1})


if __name__=='__main__': unittest.main()
