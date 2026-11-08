#!/usr/bin/env python3
"""Mutate authentic retained evidence to exercise the fixed-fork assessment boundary."""

import copy
import datetime
import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import unittest
from unittest import mock


sys.dont_write_bytecode = True
directory = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('storage_security', directory / 'check-preview-storage-security.py')
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)
with gzip.open(directory / 'fixtures/preview-storage-reviewed-evidence.json.gz', 'rb') as fixture:
    encoded = fixture.read(4 * 1024 * 1024)
DATA = json.loads(encoded)
NOW = datetime.datetime.fromisoformat(DATA['policy']['reviewedAt']) + datetime.timedelta(minutes=1)


class StorageSecurityBoundaries(unittest.TestCase):
    def values(self):
        return [copy.deepcopy(DATA[key]) for key in ('policy', 'scan', 'runtime', 'sbom', 'tool')]

    def rejects(self, change, code=1, now=NOW):
        values = self.values()
        change(values)
        with self.assertRaises((ValueError, KeyError, TypeError)):
            gate.verify(*values, code, now=now)

    def test_authentic_subject_and_all_binary_controls_qualify(self):
        summary, vex = gate.verify(*self.values(), 1, now=NOW)
        self.assertEqual((1, 1, 0, 38), (summary['rawHighFindings'], summary['fixedInReviewedFork'],
                                       summary['unresolvedSecurityFindings'], summary['runtimeObservations']))
        self.assertEqual('fixed', vex['statements'][0]['status'])
        self.assertIn(DATA['policy']['packagePurl'], json.dumps(vex['statements'][0]['products']))

    def test_a_subject_change_cannot_inherit_the_assessment(self):
        changes = (
            lambda x: x[1].update(ArtifactName='pgsty/silo:latest'),
            lambda x: x[1]['Metadata'].update(ImageID='sha256:' + '0' * 64),
            lambda x: x[1]['Metadata']['ImageConfig'].update(architecture='arm64'),
            lambda x: x[2].update(actualSourceRevision='0' * 40),
            lambda x: x[1]['Results'][-1]['Vulnerabilities'][0].update(InstalledVersion='v9.9.9'),
            lambda x: x[3]['metadata']['component'].update(purl='pkg:golang/github.com/minio/minio'),
            lambda x: x[4].update(archiveSha256='0' * 64),
        )
        for index, change in enumerate(changes):
            with self.subTest(index=index):
                self.rejects(change)

    def test_new_vulnerability_or_secret_remains_blocking(self):
        self.rejects(lambda x: x[1]['Results'][-1]['Vulnerabilities'].append(
            dict(x[1]['Results'][-1]['Vulnerabilities'][0], VulnerabilityID='CVE-2099-99999', Severity='CRITICAL')))
        self.rejects(lambda x: x[1]['Results'][-1].update(Secrets=[{'Severity': 'HIGH', 'RuleID': 'fixture-only'}]))

    def test_missing_or_empty_scan_coverage_is_rejected(self):
        self.rejects(lambda x: x[1]['Results'].pop(0))
        self.rejects(lambda x: x[1]['Results'][0].update(Packages=[]))

    def test_failed_or_missing_runtime_contracts_are_rejected(self):
        self.rejects(lambda x: x[2]['observations'].pop(14))
        self.rejects(lambda x: x[2]['observations'][17].update(errorCode='AccessDenied'))
        self.rejects(lambda x: x[2]['observations'][6].update(actualStatus=200))

    def test_cleanup_must_be_verified_boolean_evidence(self):
        for value in (False, 1, 'true'):
            with self.subTest(value=value):
                self.rejects(lambda x: x[2]['cleanup'].update(volumeRemoved=value))

    def test_tool_failure_cannot_be_classified_as_a_fixed_finding(self):
        self.rejects(lambda x: None, code=2)
        self.rejects(lambda x: None, code=0)

    def test_expired_assessment_and_stale_scan_are_rejected(self):
        expiry = datetime.datetime.fromisoformat(DATA['policy']['reviewExpiresAt'])
        self.rejects(lambda x: None, now=expiry + datetime.timedelta(seconds=1))
        self.rejects(lambda x: x[1].update(CreatedAt='2026-10-01T00:00:00Z'))

    def test_raw_advisory_disappearing_does_not_create_an_unresolved_finding(self):
        values = self.values()
        values[1]['Results'][-1]['Vulnerabilities'] = []
        summary, _ = gate.verify(*values, 0, now=NOW)
        self.assertEqual((0, 0), (summary['rawHighFindings'], summary['unresolvedSecurityFindings']))


class DerivedStorageSecurityBoundaries(unittest.TestCase):
    """Synthetic derived subjects exercise validation, never claim new live runs."""

    def values(self):
        values = [copy.deepcopy(DATA[key]) for key in ('policy', 'scan', 'runtime', 'sbom', 'tool')]
        current = json.loads((directory.parents[1] / '.gma/preview-storage-review.json').read_text())
        policy, scan, runtime, sbom, _ = values
        for key in ('baseLayers', 'fixtureImage', 'fixtureRecipeSha256'):
            policy[key] = current[key]
        image_id = 'sha256:' + 'a' * 64
        layers = policy['baseLayers'] + ['sha256:' + 'b' * 64]
        identity = {'schemaVersion': 1, 'fixtureOnly': True, 'image': policy['fixtureImage'], 'imageId': image_id,
                    'baseImage': policy['image'], 'baseImageId': policy['imageConfigDigest'],
                    'sourceRevision': policy['sourceCommit'], 'recipeSha256': policy['fixtureRecipeSha256'],
                    'baseLayers': policy['baseLayers'], 'imageLayers': layers, 'platform': 'linux/amd64',
                    'user': '10001:10001', 'entrypoint': ['/usr/bin/docker-entrypoint.sh'],
                    'command': ['server', '/data', '--address', ':9000']}
        scan['ArtifactName'] = policy['fixtureImage']
        scan['Metadata'].update(ImageID=image_id, RepoDigests=[])
        config = scan['Metadata']['ImageConfig']
        config['rootfs']['diff_ids'] = layers
        config['config'].update(User=identity['user'], Entrypoint=identity['entrypoint'], Cmd=identity['command'])
        config['config']['Labels']['io.bunkfy.fixture.recipe-sha256'] = policy['fixtureRecipeSha256']
        scan['Results'][0]['Target'] = policy['fixtureImage'] + ' (redhat 9.8)'
        runtime.update(image=policy['fixtureImage'], imageConfigDigest=image_id,
                       containerUser='10001:10001', processUser='10001', dataOwner='10001:10001')
        component = sbom['metadata']['component']
        component['name'] = policy['fixtureImage']
        for prop in component['properties']:
            if prop['name'] == 'aquasecurity:trivy:ImageID':
                prop['value'] = image_id
        return values, identity

    def test_valid_derived_subject_uses_its_own_config_identity(self):
        values, identity = self.values()
        summary, vex = gate.verify(*values, 1, now=NOW, derivation=identity)
        self.assertEqual(identity['imageId'], summary['imageConfigDigest'])
        product = vex['statements'][0]['products'][0]
        self.assertEqual('urn:bunkfy:docker-config:' + identity['imageId'], product['@id'])
        self.assertNotEqual(DATA['sbom']['metadata']['component']['purl'], product['@id'])

    def test_derived_provenance_cannot_change_or_drop_inherited_layers(self):
        changes = [
            lambda x: x.update(baseImage='unreviewed:latest'),
            lambda x: x.update(baseImageId='sha256:' + '0' * 64),
            lambda x: x.update(recipeSha256='0' * 64),
            lambda x: x.update(sourceRevision='0' * 40),
            lambda x: x.update(image='another:tag'),
            lambda x: x.update(user='0:0'),
            lambda x: x.update(platform='linux/arm64'),
            lambda x: x.update(entrypoint=['/bin/sh']),
            lambda x: x.update(command=['server', '/other']),
            lambda x: x.update(imageLayers=x['imageLayers'][1:]),
            lambda x: x.update(imageLayers=x['imageLayers'] + ['sha256:' + 'c' * 64]),
            lambda x: x.update(imageLayers=['sha256:' + 'c' * 64] + x['imageLayers'][1:]),
        ]
        for index, change in enumerate(changes):
            with self.subTest(index=index):
                values, identity = self.values()
                change(identity)
                with self.assertRaises((ValueError, KeyError, TypeError)):
                    gate.verify(*values, 1, now=NOW, derivation=identity)

    def test_derived_raw_report_runtime_and_inventory_cannot_mismatch(self):
        changes = [
            lambda x: x[1]['Metadata'].update(ImageID=DATA['policy']['imageConfigDigest']),
            lambda x: x[1]['Metadata'].update(RepoDigests=['forged@sha256:' + '0' * 64]),
            lambda x: x[1]['Metadata']['ImageConfig']['config'].update(User='0'),
            lambda x: x[1]['Metadata']['ImageConfig']['config']['Labels'].update({'io.bunkfy.fixture.recipe-sha256': '0' * 64}),
            lambda x: x[2].update(imageConfigDigest='sha256:' + '0' * 64),
            lambda x: x[2].update(processUser='0'),
            lambda x: x[2].update(dataOwner='0:0'),
            lambda x: x[2]['observations'].pop(),
            lambda x: x[2]['cleanup'].update(volumeRemoved=False),
            lambda x: x[3]['metadata']['component'].update(name=DATA['policy']['image']),
            lambda x: x[1]['Results'][-1]['Vulnerabilities'].append(
                dict(x[1]['Results'][-1]['Vulnerabilities'][0], VulnerabilityID='CVE-2099-99999')),
            lambda x: x[1]['Results'][-1].update(Secrets=[{'Severity': 'HIGH'}]),
        ]
        for index, change in enumerate(changes):
            with self.subTest(index=index):
                values, identity = self.values()
                change(values)
                with self.assertRaises((ValueError, KeyError, TypeError)):
                    gate.verify(*values, 1, now=NOW, derivation=identity)


class HtmlParserApplicabilityBoundaries(unittest.TestCase):
    def values(self):
        values, identity = DerivedStorageSecurityBoundaries().values()
        for target in ('usr/bin/silo', 'usr/bin/mcli'):
            row = next(item for item in values[1]['Results'] if item['Target'] == target)
            row.setdefault('Vulnerabilities', []).append({
                'VulnerabilityID': gate.parser_audit.CVE, 'Severity': 'HIGH',
                'PkgName': 'golang.org/x/net', 'InstalledVersion': 'v0.61.0',
                'PkgIdentifier': {'PURL': gate.parser_audit.PURL}})
        proof = {'schemaVersion': 1, 'imageId': identity['imageId'], 'goVersion': 'go1.27.3',
                 'containerNeverStarted': True, 'inspectionContainerRemoved': True, 'binaries': {}}
        for path, subject in gate.parser_audit.SUBJECTS.items():
            proof['binaries'][path] = {**subject, 'pclnMagic': '0xfffffff1',
                'goBuildInfoPresent': True, 'elfSymtabPresent': False,
                'allXNetHtmlNames': [], 'controls': ['main.main', 'runtime.goexit', 'runtime.main'],
                'targetSymbols': {name: [] for name in ('Parse', 'ParseFragment', 'ParseFragmentWithOptions', 'ParseWithOptions')}}
        return values, identity, proof

    def test_only_exact_two_binary_namespace_absences_classify_the_raw_findings(self):
        values, identity, proof = self.values()
        summary, vex = gate.verify(*values, 1, now=NOW, derivation=identity, binary_evidence=proof)
        self.assertEqual((3, 1, 2, 0), (summary['rawHighFindings'], summary['fixedInReviewedFork'],
                                      summary['parserCodeNotPresent'], summary['unresolvedSecurityFindings']))
        self.assertEqual(['fixed', 'not_affected'], [s['status'] for s in vex['statements']])
        self.assertEqual('vulnerable_code_not_present', vex['statements'][1]['justification'])
        self.assertEqual(3, sum(len(r.get('Vulnerabilities', [])) for r in values[1]['Results']))

    def test_missing_binary_inspection_remains_a_failure(self):
        values, identity, proof = self.values()
        for invalid in (None, {}, dict(proof, binaries={})):
            with self.subTest(proof=invalid):
                with self.assertRaises((ValueError, KeyError, TypeError)):
                    gate.verify(*values, 1, now=NOW, derivation=identity, binary_evidence=invalid)

    def test_changed_subject_format_or_incomplete_inline_inventory_remain_blocking(self):
        mutations = [
            lambda p: p.update(imageId='sha256:'+'0'*64),
            lambda p: p.update(goVersion='go1.28.0'),
            lambda p: p.update(containerNeverStarted=1),
            lambda p: p.update(inspectionContainerRemoved=False),
            lambda p: p['binaries'].pop('usr/bin/mcli'),
            lambda p: p['binaries']['usr/bin/silo'].update(sha256='0'*64),
            lambda p: p['binaries']['usr/bin/mcli'].update(physicalFunctionsValidated=1),
            lambda p: p['binaries']['usr/bin/silo'].update(completeFunctionNameTableSha256='0'*64),
            lambda p: p['binaries']['usr/bin/silo'].update(allXNetHtmlNames=['golang.org/x/net/html.Parse']),
            lambda p: p['binaries']['usr/bin/mcli']['targetSymbols'].update(Parse=['golang.org/x/net/html.Parse']),
            lambda p: p['binaries']['usr/bin/silo'].update(pclnMagic='0x0'),
            lambda p: p['binaries']['usr/bin/mcli'].update(controls=[]),
        ]
        for mutation in mutations:
            values, identity, proof = self.values();mutation(proof)
            with self.subTest(mutation=mutation):
                with self.assertRaises((ValueError, KeyError, TypeError)):
                    gate.verify(*values, 1, now=NOW, derivation=identity, binary_evidence=proof)

    def test_other_advisories_modules_or_targets_do_not_inherit_the_assessment(self):
        mutations = [
            lambda f: f.update(VulnerabilityID='CVE-2099-9999'),
            lambda f: f.update(PkgName='other/module'),
            lambda f: f.update(InstalledVersion='v0.62.0'),
            lambda f: f.update(Severity='CRITICAL'),
            lambda f: f['PkgIdentifier'].update(PURL='pkg:golang/golang.org/x/net@v0.60.0'),
        ]
        for mutation in mutations:
            values, identity, proof = self.values()
            row = next(r for r in values[1]['Results'] if r['Target']=='usr/bin/silo')
            mutation(row['Vulnerabilities'][-1])
            with self.assertRaises((ValueError, KeyError, TypeError)):
                gate.verify(*values, 1, now=NOW, derivation=identity, binary_evidence=proof)

    def test_duplicate_raw_finding_or_wrong_scanner_exit_still_fail(self):
        values, identity, proof = self.values()
        row = next(r for r in values[1]['Results'] if r['Target']=='usr/bin/silo')
        row['Vulnerabilities'].append(copy.deepcopy(row['Vulnerabilities'][-1]))
        with self.assertRaises(ValueError):gate.verify(*values, 1, now=NOW, derivation=identity, binary_evidence=proof)
        values, identity, proof = self.values()
        for code in (0,2):
            with self.assertRaises(ValueError):gate.verify(*values, code, now=NOW, derivation=identity, binary_evidence=proof)


class ContainerdScannerIdentity(unittest.TestCase):
    def values(self):
        values, identity = DerivedStorageSecurityBoundaries().values()
        manifest = json.dumps({'schemaVersion': 2, 'config': {'digest': identity['imageId']}})
        manifest_id = 'sha256:' + hashlib.sha256(manifest.encode()).hexdigest()
        index = json.dumps({'schemaVersion': 2, 'manifests': [
            {'digest': manifest_id, 'platform': {'os': 'linux', 'architecture': 'amd64'}}]})
        index_id = 'sha256:' + hashlib.sha256(index.encode()).hexdigest()
        identity['imageDescriptors'] = {index_id: index, manifest_id: manifest}
        values[1]['Metadata'].update(ImageID=index_id,
            RepoDigests=[identity['image'].split(':')[0] + '@' + index_id])
        for row in values[3]['metadata']['component']['properties']:
            if row['name'] == 'aquasecurity:trivy:ImageID': row['value'] = index_id
        return values, identity

    def test_actual_descriptor_chain_preserves_full_storage_gate(self):
        values, identity = self.values()
        summary, _ = gate.verify(*values, 1, now=NOW, derivation=identity)
        self.assertEqual(identity['imageId'], summary['imageConfigDigest'])

    def test_missing_modified_or_foreign_chain_and_registry_subject_are_rejected(self):
        mutations = [
            lambda v, d: d.pop('imageDescriptors'),
            lambda v, d: d['imageDescriptors'].clear(),
            lambda v, d: d['imageDescriptors'].update({v[1]['Metadata']['ImageID']: '{}'}),
            lambda v, d: v[1]['Metadata'].update(RepoDigests=['foreign@' + v[1]['Metadata']['ImageID']]),
            lambda v, d: v[1]['Metadata'].update(ImageID='sha256:' + '0' * 64),
            lambda v, d: next(row for row in v[3]['metadata']['component']['properties']
                              if row['name'] == 'aquasecurity:trivy:ImageID').update(value='sha256:' + '0' * 64),
        ]
        for mutate in mutations:
            values, identity = self.values()
            mutate(values, identity)
            with self.subTest(mutation=mutate), self.assertRaises(ValueError):
                gate.verify(*values, 1, now=NOW, derivation=identity)

    def test_a_validly_hashed_manifest_cannot_substitute_another_configuration(self):
        values, identity = self.values()
        raw = json.dumps({'schemaVersion': 2, 'config': {'digest': 'sha256:' + '0' * 64}})
        subject = 'sha256:' + hashlib.sha256(raw.encode()).hexdigest()
        identity['imageDescriptors'] = {subject: raw}
        values[1]['Metadata'].update(ImageID=subject, RepoDigests=[identity['image'].split(':')[0] + '@' + subject])
        with self.assertRaisesRegex(ValueError, 'different configuration'):
            gate.verify(*values, 1, now=NOW, derivation=identity)


class ParserInspectionContainerOwnership(unittest.TestCase):
    def exercise(self, fail_copy=False, wrong_image=False, containerd=False):
        values, identity, proof = HtmlParserApplicabilityBoundaries().values()
        image=identity['imageId']; container='e'*64; commands=[]; removed=False
        def process(arguments, **kwargs):
            nonlocal removed
            commands.append(arguments)
            self.assertEqual('docker', arguments[0])
            operation=arguments[1]
            if operation=='create':
                self.assertEqual(identity['image'] if containerd else image,arguments[-1])
                for required in ('none','--read-only','type=tmpfs,destination=/data'):self.assertIn(required,arguments)
                return type('Result',(),dict(returncode=0,stdout=(container+'\n').encode(),stderr=b''))()
            if operation=='container' and arguments[2]=='inspect':
                self.assertEqual(container,arguments[-1])
                if removed:return type('Result',(),dict(returncode=1,stdout=b'',stderr=('Error: No such container: '+container).encode()))()
                row={'Id':container,'Image':('sha256:'+'f'*64 if containerd else ('sha256:'+'0'*64 if wrong_image else image)),
                     'State':{'Status':'created','Running':False}}
                return type('Result',(),dict(returncode=0,stdout=json.dumps([row]).encode(),stderr=b''))()
            if operation=='cp':
                self.assertTrue(arguments[2].startswith(container+':/usr/bin/'))
                if fail_copy:return type('Result',(),dict(returncode=1,stdout=b'',stderr=b'copy failed'))()
                Path(arguments[-1]).write_bytes(b'known-test-binary')
                return type('Result',(),dict(returncode=0,stdout=b'',stderr=b''))()
            if operation=='rm':
                self.assertEqual(['docker','rm','--volumes',container],arguments);removed=True
                return type('Result',(),dict(returncode=0,stdout=container.encode(),stderr=b''))()
            self.fail('Target execution or unowned Docker operation: '+repr(arguments))
        def inspect(path):return dict(proof['binaries']['usr/bin/'+path.name],path=str(path))
        def file_digest(stream,algorithm):
            digest_value=gate.parser_audit.SUBJECTS['usr/bin/'+Path(stream.name).name]['sha256']
            return type('Digest',(),{'hexdigest':lambda self:digest_value})()
        with mock.patch.object(gate.parser_audit.subprocess,'run',side_effect=process), \
             mock.patch.object(gate.parser_audit,'inspect',side_effect=inspect), \
             mock.patch.object(gate.parser_audit.hashlib,'file_digest',side_effect=file_digest), \
             mock.patch.object(gate.parser_audit.image_identity,'image_config_digest',return_value=('sha256:'+'0'*64 if wrong_image else image)):
            reference = identity['image'] if containerd else None
            if fail_copy or wrong_image:
                with self.assertRaises(ValueError):gate.parser_audit.collect(image, image_reference=reference)
            else:
                actual=gate.parser_audit.collect(image, image_reference=reference)
                self.assertTrue(actual['containerNeverStarted']);self.assertTrue(actual['inspectionContainerRemoved'])
        self.assertTrue(removed)
        self.assertFalse(any(c[1] in ('run','start','exec') for c in commands))
        return commands

    def test_same_image_binaries_are_copied_without_running_the_container(self):
        commands=self.exercise();self.assertEqual(2,sum(c[1]=='cp' for c in commands))

    def test_containerd_manifest_id_resolves_to_the_exact_reviewed_configuration(self):
        self.exercise(containerd=True)

    def test_containerd_foreign_configuration_is_rejected_before_copy(self):
        commands=self.exercise(containerd=True, wrong_image=True)
        self.assertFalse(any(c[1]=='cp' for c in commands))

    def test_copy_failure_still_removes_only_the_exact_owned_container(self):
        self.exercise(fail_copy=True)

    def test_a_foreign_image_is_rejected_before_copy_and_owned_container_removed(self):
        commands=self.exercise(wrong_image=True);self.assertFalse(any(c[1]=='cp' for c in commands))


if __name__ == '__main__':
    unittest.main()

