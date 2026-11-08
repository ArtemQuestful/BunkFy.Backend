#!/usr/bin/env python3
"""Read the actual amd64 configuration digest, independent of Docker image-store IDs."""
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import tarfile
import tempfile


def config_digest(archive, proof_output=None):
    with tarfile.open(archive) as tar:
        def read(name):
            matches = [member for member in tar.getmembers() if member.name == name]
            if len(matches) != 1 or not matches[0].isfile() or matches[0].size > 4 * 1024**2:
                raise ValueError('Missing, duplicated or oversized image metadata')
            return tar.extractfile(matches[0]).read()

        manifest = json.loads(read('manifest.json'))
        if not isinstance(manifest, list) or len(manifest) != 1:
            raise ValueError('Exactly one exported platform image is required')
        name = manifest[0]['Config']
        if not isinstance(name, str) or PurePosixPath(name).is_absolute() or '..' in PurePosixPath(name).parts:
            raise ValueError('Unsafe image configuration path')
        match = re.fullmatch(r'(?:blobs/sha256/([a-f0-9]{64})|([a-f0-9]{64})\.json)', name)
        if not match:
            raise ValueError('Configuration path must identify its SHA256 content')
        raw = read(name)
        digest = hashlib.sha256(raw).hexdigest()
        if digest != (match[1] or match[2]):
            raise ValueError('Configuration content does not match its digest')
        config = json.loads(raw)
        if config.get('os') != 'linux' or config.get('architecture') != 'amd64':
            raise ValueError('Only the qualified Linux amd64 platform is supported')
        if proof_output is not None:
            descriptors = {}
            if any(member.name == 'index.json' for member in tar.getmembers()):
                index = json.loads(read('index.json'))
                for _ in range(4):
                    candidates = [row for row in index['manifests'] if
                                  row.get('platform', {'os': 'linux', 'architecture': 'amd64'}) ==
                                  {'os': 'linux', 'architecture': 'amd64'}]
                    if len(candidates) != 1:
                        raise ValueError('One exported Linux amd64 descriptor is required')
                    subject = candidates[0]['digest']
                    if not re.fullmatch(r'sha256:[a-f0-9]{64}', subject):
                        raise ValueError('Invalid OCI descriptor digest')
                    raw_descriptor = read('blobs/sha256/' + subject[7:])
                    if 'sha256:' + hashlib.sha256(raw_descriptor).hexdigest() != subject:
                        raise ValueError('OCI descriptor bytes do not match their digest')
                    descriptors[subject] = raw_descriptor.decode('utf-8')
                    index = json.loads(raw_descriptor)
                    if 'config' in index:
                        if index['config']['digest'] != 'sha256:' + digest:
                            raise ValueError('OCI manifest references another configuration')
                        break
                else:
                    raise ValueError('OCI descriptor chain exceeds its bound')
            Path(proof_output).write_text(json.dumps(descriptors) + '\n')
        return 'sha256:' + digest


def image_config_digest(image, proof_output=None):
    with tempfile.TemporaryDirectory(prefix='bunkfy-image-config-') as directory:
        archive = directory + '/image.tar'
        platform = ['--platform', 'linux/amd64'] if proof_output is None else []
        subprocess.run(['docker', 'image', 'save', *platform, '--output', archive, image],
                       check=True, stdout=subprocess.DEVNULL, timeout=120)
        return config_digest(archive, proof_output)


if __name__ == '__main__':
    if len(sys.argv) not in (2, 3):
        raise SystemExit('Usage: image-config-digest.py IMAGE [DESCRIPTOR_PROOF_OUTPUT]')
    print(image_config_digest(sys.argv[1], sys.argv[2] if len(sys.argv) == 3 else None))
