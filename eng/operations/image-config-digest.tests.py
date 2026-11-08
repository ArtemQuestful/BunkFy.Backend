#!/usr/bin/env python3
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('image_config', Path(__file__).with_name('image-config-digest.py'))
identity = importlib.util.module_from_spec(spec)
spec.loader.exec_module(identity)


class ImageConfigurationIdentity(unittest.TestCase):
    def archive(self, *, layout='oci', architecture='amd64', corrupt=False, duplicate=False, count=1, unsafe=False):
        raw = json.dumps({'os': 'linux', 'architecture': architecture, 'rootfs': {'diff_ids': []}}).encode()
        digest = hashlib.sha256(raw).hexdigest()
        name = ('blobs/sha256/' + digest) if layout == 'oci' else digest + '.json'
        if unsafe:
            name = '../' + name
        temporary = tempfile.NamedTemporaryFile(suffix='.tar')
        with tarfile.open(fileobj=temporary, mode='w') as tar:
            entries = [('manifest.json', json.dumps([{'Config': name}] * count).encode()),
                       (name, raw + (b' ' if corrupt else b''))]
            if duplicate:
                entries.append((name, raw))
            for path, contents in entries:
                member = tarfile.TarInfo(path)
                member.size = len(contents)
                tar.addfile(member, io.BytesIO(contents))
        temporary.seek(0)
        return temporary, 'sha256:' + digest

    def test_reads_exact_config_in_both_archive_layouts(self):
        for layout in ('oci', 'legacy'):
            with self.subTest(layout=layout):
                archive, expected = self.archive(layout=layout)
                with archive:
                    self.assertEqual(expected, identity.config_digest(archive.name))

    def test_rejects_modified_duplicate_wrong_platform_or_ambiguous_metadata(self):
        for options in ({'corrupt': True}, {'duplicate': True}, {'architecture': 'arm64'},
                        {'count': 0}, {'count': 2}, {'unsafe': True}):
            with self.subTest(options=options):
                archive, _ = self.archive(**options)
                with archive, self.assertRaises(ValueError):
                    identity.config_digest(archive.name)


if __name__ == '__main__':
    unittest.main()
