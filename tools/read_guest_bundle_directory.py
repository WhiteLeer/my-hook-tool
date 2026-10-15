"""Build exact CAB-to-BLOCK directory evidence from bounded ENCR headers, without reading payloads."""
import argparse
import hashlib
import json
import re
import struct
import subprocess
from pathlib import Path
import lz4.block


def decode(data):
    if data[:4]!=b'BHD1': raise ValueError('Missing header-stream signature')
    # Preserve the original capture; this removes only the old helper's framed diagnostic text.
    # Legacy exec-out mixed its stderr line into the final buffered header.
    legacy=list(re.finditer(rb'files=\d+ records=\d+ errors=\d+ header_bytes=\d+\n',data[max(0,len(data)-512):]))
    if legacy:
        if len(legacy)!=1: raise ValueError('Ambiguous legacy transport diagnostics')
        start=max(0,len(data)-512)+legacy[0].start();end=max(0,len(data)-512)+legacy[0].end()
        data=data[:start]+data[end:]
    pos=4
    records=[]
    errors=[]
    while pos<len(data):
        length=struct.unpack_from('<H',data,pos)[0];pos+=2
        if length==0:
            if len(data)-pos!=20: raise ValueError('Invalid terminal diagnostics frame')
            files,bundles,failures,header_bytes=struct.unpack_from('<IIIQ',data,pos)
            break
        name=data[pos:pos+length].decode('ascii');pos+=length
        if not re.fullmatch(r'[0-9a-f]{32}\.block',name): raise ValueError('Invalid source filename')
        offset,size,flags,usize,csize=struct.unpack_from('<QQIII',data,pos);pos+=28
        raw=data[pos:pos+csize];pos+=csize
        if len(raw)!=csize: raise ValueError('Truncated header stream')
        try:
            mode=flags&63
            if mode==0: info=raw
            elif mode in (2,3): info=lz4.block.decompress(raw,uncompressed_size=usize)
            else: raise ValueError('Unsupported header compression '+str(mode))
            if len(info)!=usize: raise ValueError('Header size mismatch')
            blocks=struct.unpack_from('>I',info)[0]
            p=4+10*blocks
            if p+4>len(info): raise ValueError('Block table exceeds header')
            nodes=struct.unpack_from('>I',info,p)[0];p+=4
            if nodes>4096: raise ValueError('Directory node budget')
            for _ in range(nodes):
                data_offset,data_size,node_flags=struct.unpack_from('>QQI',info,p);p+=20
                end=info.index(b'\0',p)
                path=info[p:end].decode('utf-8');p=end+1
                records.append(dict(block=name,bundleOffset=offset,bundleBytes=size,path=path,
                    dataOffset=data_offset,dataSize=data_size,nodeFlags=node_flags,headerSha256=hashlib.sha256(raw).hexdigest()))
            if p!=len(info): raise ValueError('Unconsumed directory header')
        except (ValueError,struct.error,UnicodeError,lz4.block.LZ4BlockError) as e:
            errors.append(dict(block=name,bundleOffset=offset,error=str(e)))
    return records,errors


def main(a):
    a.output.mkdir(parents=True,exist_ok=True)
    result=subprocess.run([str(a.adb),'-P',str(a.port),'-s',a.serial,'exec-out',a.helper,a.guest_root],capture_output=True,timeout=120)
    if result.returncode or len(result.stdout)>320*1024*1024: raise ValueError('Bounded filesystem scan failed: '+result.stderr.decode(errors='replace'))
    stream=a.output/'header-scan.bin';stream.write_bytes(result.stdout)
    (a.output/'scan.log').write_bytes(result.stderr)
    records,errors=decode(result.stdout)
    report=dict(schema='my-hook.encr-directory-index.v1',guestRoot=a.guest_root,
        scanSha256=hashlib.sha256(result.stdout).hexdigest(),scanStatus=result.stderr.decode(),records=records,errors=errors,
        boundary='Directory identities only. Selected full BLOCK bytes must still match the current BlockV digest and size.')
    (a.output/'cab-directory.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(json.dumps(dict(records=len(records),headersBytes=len(result.stdout),errors=len(errors),scanStatus=report['scanStatus'])))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--adb',type=Path,required=True)
    p.add_argument('--serial',required=True)
    p.add_argument('--port',type=int,default=5037)
    p.add_argument('--helper',required=True)
    p.add_argument('--guest-root',required=True)
    p.add_argument('--output',type=Path,required=True)
    main(p.parse_args())
