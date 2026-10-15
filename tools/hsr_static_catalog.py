"""Strict observed HSR 4.6 catalog layouts, without inventing meanings for opaque fields."""
import hashlib
import struct
from pathlib import Path

import xxhash


def design_index(data):
    if len(data) < 16:
        raise ValueError('Truncated DesignV header')
    tag, revision, count, total = struct.unpack_from('>4I', data)
    if (tag, revision) != (255, 4) or count > 256 or total > 1000000:
        raise ValueError('Unsupported DesignV layout')
    pos = 16
    files = []
    for _ in range(count):
        start = pos
        if pos + 36 > len(data):
            raise ValueError('Truncated file record')
        parent, digest, size, number = struct.unpack_from('>Q16sQI', data, pos)
        pos += 36
        if number > total or pos + number * 16 + 3 > len(data):
            raise ValueError('Truncated item table')
        items = []
        end = 0
        for i in range(number):
            key, length, offset = struct.unpack_from('>QII', data, pos)
            if offset != end or offset + length > size:
                raise ValueError('Noncontiguous or out-of-file item range')
            items.append(dict(index=i, recordOffset=pos, key=f'{key:016x}', offset=offset, size=length))
            pos += 16
            end = offset + length
        if end != size:
            raise ValueError('Item ranges do not cover declared file size')
        length = struct.unpack_from('>H', data, pos)[0]
        pos += 2
        if pos + length + 1 > len(data) or data[pos + length] != 0x80:
            raise ValueError('Unsupported DesignV record tail')
        text = data[pos:pos + length].decode('utf-8')
        pos += length + 1
        files.append(dict(recordOffset=start, parentKey=f'{parent:016x}', md5=digest.hex(),
                          size=size, text=text, items=items))
    if pos != len(data) or sum(len(f['items']) for f in files) != total:
        raise ValueError('Catalog not completely consumed')
    return files


class AssetCatalog:
    def __init__(self, data):
        self.data = data
        if data[:8] != bytes.fromhex('5352414d00050001'):
            raise ValueError('Unsupported AsbV header')
        self.tables = [struct.unpack_from('<4I', data, 40 + i * 16) for i in range(8)]
        end = 168
        for reserved, offset, count, stride in self.tables:
            if reserved != 0 or offset != end or stride == 0:
                raise ValueError('Noncontiguous AsbV sections')
            end = offset + count * stride
        if end != len(data) or [t[3] for t in self.tables] != [24, 4, 32, 4, 8, 12, 4, 8]:
            raise ValueError('AsbV layout not completely consumed')
        self.paths = {}
        _, start, count, _ = self.tables[2]
        dependency_end = 0
        for index in range(count):
            record, block, offset, size, key, deps, number, opaque = struct.unpack_from('<4IQIHH', data, start + index * 32)
            if record != index or block >= self.tables[0][2] or (number and deps != dependency_end) or (not number and deps != 0):
                raise ValueError('Invalid bundle/dependency record')
            block_size=struct.unpack_from('<I',data,self.tables[0][1]+block*24+20)[0]
            if size==0 or offset+size>block_size:
                raise ValueError('Bundle range exceeds declared block size')
            if number:
                dependency_end = deps + number
            if key in self.paths:
                raise ValueError('Duplicate path hash: ambiguous lookup')
            self.paths[key] = dict(index=index, recordOffset=start + index * 32,
                                   blockIndex=block, bundleOffset=offset, bundleBytes=size,
                                   dependencyStart=deps, dependencyCount=number, opaqueTail=opaque)
        if dependency_end != self.tables[1][2]:
            raise ValueError('Dependencies do not cover section 1')
        _, start, count, _ = self.tables[1]
        if any(x[0] >= self.tables[2][2] for x in struct.iter_unpack('<I', data[start:start + count * 4])):
            raise ValueError('Dependency index out of range')

    def lookup(self, path, directory):
        key = xxhash.xxh64(path.encode('utf-8')).intdigest()
        if key not in self.paths:
            raise ValueError('Full source path absent from AsbV: ' + path)
        entry = dict(self.paths[key])
        pos = self.tables[0][1] + entry['blockIndex'] * 24
        md5 = b''.join(self.data[pos + j:pos + j + 4][::-1] for j in range(0, 16, 4)).hex()
        opaque, size = struct.unpack_from('<2I', self.data, pos + 16)
        matches = [r for r in directory if r['block'] == md5 + '.block'
                   and r['bundleOffset'] == entry['bundleOffset'] and r['bundleBytes'] == entry['bundleBytes']
                   and not r['path'].endswith('.resS')]
        if len(matches) != 1:
            raise ValueError('Bundle range must identify exactly one CAB')
        return dict(entry, sourcePath=path, pathHash=f'{key:016x}', block=md5 + '.block',
                    blockBytes=size, opaqueBlockWord=opaque, sourceFile=matches[0]['path'])


def verify_content_file(path, expected_md5, size):
    path = Path(path)
    if path.stat().st_size != size:
        raise ValueError('Source file size differs from current catalog')
    with path.open('rb') as stream:
        value = hashlib.file_digest(stream, 'md5').hexdigest()
    if value != expected_md5:
        raise ValueError('Source file digest differs from current catalog')
