"""Narrow version-string adapter; source bytes and opaque suffix remain unchanged."""
from unittest.mock import patch
import UnityPy
from UnityPy.files.SerializedFile import SerializedFile
from UnityPy.helpers.UnityVersion import UnityVersion


def load_container(path):
    original=SerializedFile.set_version
    def version(self,value):
        if value=='2019.4.34f1\n2':
            self.unity_version=value
            self.version=UnityVersion.from_str('2019.4.34f1')
        else:
            original(self,value)
    with patch.object(SerializedFile,'set_version',version):
        return UnityPy.load(str(path))
