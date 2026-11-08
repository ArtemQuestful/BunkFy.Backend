#!/usr/bin/env python3
"""Bind exact provider binaries to complete physical/inline Go package absence.

The pinned Go1.27.3 linker walkFuncs/generateFuncnametab contract includes all
physical and inlined functions. Validate every physical name reference, then
inspect the entire bounded function-name table. The advisory's whole HTML
package is absent in these two exact binaries, although x/net remains in the
module inventory for other packages. This is not govulncheck's stripped-binary
fallback and never treats an unrecognized binary or new finding as reviewed.
"""
import stat
import importlib.util
import subprocess
import tempfile
import uuid
import hashlib
import json
from pathlib import Path
import struct


_identity_spec = importlib.util.spec_from_file_location('image_config', Path(__file__).with_name('image-config-digest.py'))
image_identity = importlib.util.module_from_spec(_identity_spec)
_identity_spec.loader.exec_module(image_identity)

def inspect(path):
    with path.open('rb') as stream:
        raw = stream.read(128 * 1024 * 1024 + 1)
    if len(raw) > 128 * 1024 * 1024:
        raise ValueError('ELF exceeds analysis bound')
    if raw[:7] != b'\x7fELF\x02\x01\x01':
        raise ValueError('Expected ELF64 little-endian current Go executable')
    header = struct.unpack_from('<HHIQQQIHHHHHH', raw, 16)
    kind, machine, version, _, _, shoff, _, _, _, _, shentsize, shnum, shstr = header
    if (kind != 2 or machine != 62 or version != 1 or shentsize != 64
            or not 1 <= shnum <= 128 or not 0 < shstr < shnum
            or shoff + shnum * shentsize > len(raw)):
        raise ValueError('Unsupported or incomplete ELF section inventory')
    sections = [struct.unpack_from('<IIQQQQIIQQ', raw, shoff + n * 64) for n in range(shnum)]
    strings = sections[shstr]
    section_names = raw[strings[4]:strings[4] + strings[5]]
    inventory = {}
    for row in sections:
        end = section_names.find(b'\0', row[0])
        if end < row[0]:
            raise ValueError('Unterminated ELF section name')
        name = section_names[row[0]:end].decode('ascii')
        if name in inventory:
            raise ValueError('Duplicate ELF section')
        if row[1] != 8 and row[4] + row[5] > len(raw):
            raise ValueError('ELF section exceeds file bound')
        inventory[name] = row
    text = inventory['.text']
    section = inventory['.gopclntab']
    data = raw[section[4]:section[4] + section[5]]
    magic, pad1, pad2, quantum, pointer = struct.unpack_from('<IBBBB', data)
    if (magic, pad1, pad2, quantum, pointer) != (0xfffffff1, 0, 0, 1, 8):
        raise ValueError('Unsupported PCLN format')
    nfunc, nfiles, unused_text, namesoff, cuoff, fileoff, pcoff, funcoff = struct.unpack_from('<8Q', data, 8)
    if (not 1 <= nfunc <= 250000 or not 1 <= nfiles <= 250000
            or not 72 <= namesoff < cuoff <= fileoff <= pcoff <= funcoff < len(data)):
        raise ValueError('Invalid PCLN inventory or offsets')
    name_bytes = data[namesoff:cuoff]
    if len(name_bytes) > 16 * 1024 * 1024:
        raise ValueError('Function-name table exceeds the reviewed size bound')
    if not name_bytes.endswith(b'\0'):
        raise ValueError('Function-name table is unterminated')
    names_by_offset = {}
    offset = 0
    while offset < len(name_bytes):
        end = name_bytes.index(b'\0', offset)
        if end != offset:
            names_by_offset[offset] = name_bytes[offset:end].decode('utf-8')
        offset = end + 1
        if len(names_by_offset) > 500000:
            raise ValueError('Function-name inventory exceeds its bound')
    table_bytes = nfunc * 8 + 4
    if funcoff + table_bytes > len(data):
        raise ValueError('Function table is incomplete')
    previous = -1
    physical = set()
    for index in range(nfunc):
        entry, location = struct.unpack_from('<II', data, funcoff + index * 8)
        if entry <= previous or location < table_bytes or funcoff + location + 44 > len(data):
            raise ValueError('Function-table entry/order is invalid')
        func_entry, name = struct.unpack_from('<Ii', data, funcoff + location)
        if func_entry != entry or name not in names_by_offset:
            raise ValueError('Function body/name reference does not match PCLN inventory')
        physical.add(names_by_offset[name])
        previous = entry
    last = struct.unpack_from('<I', data, funcoff + nfunc * 8)[0]
    if last <= previous or last > text[5]:
        raise ValueError('Function-table terminal PC exceeds text section')
    all_names = set(names_by_offset.values())
    controls = {'runtime.main', 'runtime.goexit', 'main.main'}
    if not controls <= physical:
        raise ValueError('Required independently identifiable executable controls disappeared')
    prefix = 'golang.org/x/net/html'
    wanted = ['Parse', 'ParseFragment', 'ParseFragmentWithOptions', 'ParseWithOptions']
    symbols = {symbol: sorted(name for name in all_names
                             if name == prefix + '.' + symbol
                             or name.startswith((prefix + '.' + symbol + '.', prefix + '.' + symbol + '[')))
               for symbol in wanted}
    return {'path': str(path), 'bytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest(),
            'pclnMagic': hex(magic), 'physicalFunctionsValidated': nfunc, 'sourceFiles': nfiles,
            'functionNameEntries': len(names_by_offset), 'uniqueFunctionNames': len(all_names),
            'namesNotInPhysicalTable': len(all_names - physical),
            'physicalTableSha256': hashlib.sha256(data[funcoff:funcoff + table_bytes]).hexdigest(),
            'completeFunctionNameTableSha256': hashlib.sha256(name_bytes).hexdigest(),
            'goBuildInfoPresent': '.go.buildinfo' in inventory,
            'elfSymtabPresent': '.symtab' in inventory,
            'controls': sorted(controls), 'targetSymbols': symbols,
            'allXNetHtmlNames': sorted(name for name in all_names if prefix in name),
            'inlineNameControlExamples': sorted(all_names - physical)[:12],
            'scope': 'Exact Go1.27.3 complete physical/inline function-name inventory; only the pinned x/net/html package-absence assessment.'}


# Exact binaries from the unchanged, digest-pinned c3ad provider base. Unknown
# binaries, formats, namespaces and new advisories never inherit this assessment.
SUBJECTS = {
    'usr/bin/silo': {
        'sha256': '6049a2eda411fd5046eec542721599177874193a6a5750d1c3c1540cce65a2d1',
        'physicalFunctionsValidated': 126854,
        'completeFunctionNameTableSha256': '0df9852e0990693b98aa0f1d87e80883a45224b47ed43ac3eb9304e7b6682833',
    },
    'usr/bin/mcli': {
        'sha256': '8afa1243115737e25746e84a59b37d61a29d5fd6e6b76cc3dbccbff2a20380ba',
        'physicalFunctionsValidated': 33911,
        'completeFunctionNameTableSha256': 'f9cc6ab96c341fcca9a4b14231ddf88688a84efc70d2b71e380016e42358c89b',
    },
}
CVE = 'CVE-2026-78661'
PURL = 'pkg:golang/golang.org/x/net@v0.61.0'
PRIMARY_ADVISORY = 'https://pkg.go.dev/vuln/GO-2026-6860'
LINKER_CONTRACT = 'https://github.com/golang/go/blob/go1.27.3/src/cmd/link/internal/ld/pcln.go'


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate(proof, image_id):
    require(isinstance(proof, dict) and proof.get('schemaVersion') == 1
            and proof.get('imageId') == image_id and proof.get('goVersion') == 'go1.27.3'
            and proof.get('containerNeverStarted') is True and proof.get('inspectionContainerRemoved') is True,
            'Actual same-image, never-started binary inspection and owned cleanup are required.')
    binaries = proof.get('binaries')
    require(isinstance(binaries, dict) and set(binaries) == set(SUBJECTS),
            'Both exact provider binary inventories are required.')
    for path, expected in SUBJECTS.items():
        row = binaries[path]
        require(isinstance(row, dict) and all(row.get(k) == v for k, v in expected.items())
                and row.get('pclnMagic') == '0xfffffff1' and row.get('goBuildInfoPresent') is True
                and row.get('elfSymtabPresent') is False and row.get('allXNetHtmlNames') == []
                and row.get('controls') == ['main.main', 'runtime.goexit', 'runtime.main']
                and row.get('targetSymbols') == {k: [] for k in ('Parse', 'ParseFragment', 'ParseFragmentWithOptions', 'ParseWithOptions')},
                'The exact reviewed Go physical/inline namespace-absence proof changed: ' + path)
    return proof


def collect(image_id, image_reference=None):
    # Creating and copying from this disposable container does not run its entry
    # point or target binaries. Override /data so no anonymous data volume exists.
    import re
    require(re.fullmatch(r'sha256:[0-9a-f]{64}', image_id) is not None, 'Invalid immutable image configuration identity.')
    def docker(*args):
        result = subprocess.run(['docker', *args], capture_output=True, timeout=90)
        if result.returncode:
            raise ValueError('Owned binary-inspection Docker operation failed: ' + args[0])
        return result.stdout
    container = docker('create', '--name', 'bunkfy-parser-inspect-' + uuid.uuid4().hex,
                       '--network', 'none', '--read-only', '--mount', 'type=tmpfs,destination=/data', image_reference or image_id).decode().strip()
    require(re.fullmatch(r'[0-9a-f]{64}', container) is not None, 'Invalid owned inspection container identity.')
    proof = {'schemaVersion': 1, 'imageId': image_id, 'goVersion': 'go1.27.3',
             'containerNeverStarted': False, 'inspectionContainerRemoved': False, 'binaries': {}}
    try:
        identity = json.loads(docker('container', 'inspect', container))
        require(len(identity) == 1 and identity[0]['Id'] == container
                and identity[0]['State']['Status'] == 'created' and identity[0]['State']['Running'] is False,
                'The owned inspection container must never have started.')
        actual_config = (identity[0]['Image'] if image_reference is None else
                         image_identity.image_config_digest(identity[0]['Image']))
        require(actual_config == image_id, 'The actual inspection container configuration differs from the reviewed image.')
        proof['containerNeverStarted'] = True
        with tempfile.TemporaryDirectory(prefix='bunkfy-parser-binaries-') as directory:
            for path in SUBJECTS:
                destination = Path(directory) / Path(path).name
                docker('cp', container + ':/' + path, str(destination))
                require(stat.S_ISREG(destination.lstat().st_mode) and destination.stat().st_nlink == 1,
                        'Only a regular, unlinked binary copy may be inspected.')
                require(destination.stat().st_size <= 128 * 1024 * 1024, 'Provider binary exceeds its bound.')
                with destination.open('rb') as stream:
                    require(hashlib.file_digest(stream, 'sha256').hexdigest() == SUBJECTS[path]['sha256'],
                            'Unknown provider binary cannot inherit the parser assessment.')
                row = inspect(destination)
                row.pop('path')
                proof['binaries'][path] = row
    finally:
        docker('rm', '--volumes', container)
        remaining = subprocess.run(['docker', 'container', 'inspect', container], capture_output=True, timeout=30)
        require(remaining.returncode != 0 and container.encode() in remaining.stderr
                and (b'No such container' in remaining.stderr or b'No such object' in remaining.stderr),
                'Owned inspection container removal must be independently verified.')
        proof['inspectionContainerRemoved'] = True
    return validate(proof, image_id)
