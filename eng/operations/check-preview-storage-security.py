#!/usr/bin/env python3
"""Qualify the pinned Preview fixture and retain a scoped fixed-fork assessment."""

import argparse
from collections import Counter
import datetime
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import urllib.request


_parser_spec = importlib.util.spec_from_file_location('preview_storage_parser', Path(__file__).with_name('inspect-preview-storage-parser.py'))
parser_audit = importlib.util.module_from_spec(_parser_spec)
_parser_spec.loader.exec_module(parser_audit)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def bind_scanner_configuration(metadata, config_digest, derivation):
    """Docker's containerd ID is an OCI index; retain and hash its real descriptor chain."""
    subject = metadata['ImageID']
    if subject == config_digest:
        return False
    require(derivation is not None, 'Image configuration digest changed.')
    descriptors = derivation.get('imageDescriptors', {})
    require(isinstance(descriptors, dict) and 0 < len(descriptors) <= 4,
            'Actual OCI descriptor bytes are required for a containerd scanner identity.')
    seen = set()
    while subject != config_digest:
        require(subject not in seen and subject in descriptors and len(seen) < 4,
                'Missing or cyclic OCI image identity chain.')
        seen.add(subject)
        raw = descriptors[subject]
        require(isinstance(raw, str) and len(raw.encode()) <= 64 * 1024
                and 'sha256:' + hashlib.sha256(raw.encode()).hexdigest() == subject,
                'OCI descriptor content does not match the scanner subject.')
        document = json.loads(raw)
        require(document['schemaVersion'] == 2, 'Unsupported OCI descriptor schema.')
        if 'config' in document:
            subject = document['config']['digest']
            require(subject == config_digest, 'OCI manifest references a different configuration.')
        else:
            matches = [row for row in document['manifests'] if
                       row.get('platform') == {'os': 'linux', 'architecture': 'amd64'}]
            require(len(matches) == 1, 'Exactly one qualified amd64 manifest is required.')
            subject = matches[0]['digest']
    return True


def derived_subject(policy, identity):
    require(identity['schemaVersion'] == 1 and identity['fixtureOnly'] is True
            and identity['image'] == policy['fixtureImage'] == 'bunkfy-provider-silo:c3ad836aed0d-nonroot-v1'
            and re.fullmatch(r'sha256:[a-f0-9]{64}', identity['imageId']) is not None,
            'Only the reviewed local non-root fixture is supported.')
    require(identity['baseImage'] == policy['image']
            and identity['baseImageId'] == policy['imageConfigDigest']
            and identity['sourceRevision'] == policy['sourceCommit']
            and identity['recipeSha256'] == policy['fixtureRecipeSha256'],
            'Derived fixture source, base configuration or reviewed recipe changed.')
    require(identity['baseLayers'] == policy['baseLayers']
            and len(identity['imageLayers']) == len(policy['baseLayers']) + 1
            and identity['imageLayers'][:-1] == policy['baseLayers']
            and re.fullmatch(r'sha256:[a-f0-9]{64}', identity['imageLayers'][-1]) is not None,
            'Derived fixture must retain every pinned base layer and one ownership-only recipe layer.')
    require(identity['platform'] == 'linux/amd64' and identity['user'] == '10001:10001'
            and identity['entrypoint'] == ['/usr/bin/docker-entrypoint.sh']
            and identity['command'] == ['server', '/data', '--address', ':9000'],
            'Derived fixture user, entrypoint, data directory or command changed.')
    return identity['image'], identity['imageId']


def verify(policy, scan, runtime, sbom, tool, scan_exit_code, now=None, derivation=None, binary_evidence=None):
    now = now or datetime.datetime.now(datetime.timezone.utc)
    issued = datetime.datetime.fromisoformat(policy['reviewedAt'])
    expiry = datetime.datetime.fromisoformat(policy['reviewExpiresAt'])
    require(issued <= now < expiry and expiry - issued <= datetime.timedelta(days=30),
            'Storage assessment is expired, future-dated or exceeds thirty days.')
    require(policy['schemaVersion'] == 1 and policy['status'] == 'fixed'
            and policy['vulnerability'] == 'CVE-2026-39414', 'Only the reviewed fixed-fork assessment is supported.')
    require(policy['platform'] == 'linux/amd64', 'Only the qualified Linux amd64 fixture is supported.')
    image, config_digest = ((policy['image'], policy['imageConfigDigest']) if derivation is None
                            else derived_subject(policy, derivation))
    require(scan['SchemaVersion'] == 2 and scan['ArtifactType'] == 'container_image'
            and scan['ArtifactName'] == image, 'Raw scanner subject does not match the reviewed immutable image.')
    created = datetime.datetime.fromisoformat(scan['CreatedAt'].replace('Z', '+00:00'))
    require(datetime.timedelta(0) <= now - created <= datetime.timedelta(hours=2), 'A fresh raw image scan is required.')
    metadata = scan['Metadata']
    containerd = bind_scanner_configuration(metadata, config_digest, derivation)
    config = metadata['ImageConfig']
    require(config['architecture'] == 'amd64' and config['os'] == 'linux'
            and config['config']['Labels']['org.opencontainers.image.revision'] == policy['sourceCommit'],
            'Image platform or reviewed source revision changed.')
    image_digest = policy['image'].rsplit('@', 1)[1]
    if derivation is None:
        require(metadata['RepoDigests'] == ['pgsty/silo@' + image_digest], 'Registry image subject changed.')
    else:
        expected_repos = ([image.split(':')[0] + '@' + metadata['ImageID']] if containerd else [])
        require(metadata.get('RepoDigests', []) == expected_repos
                and config['rootfs']['diff_ids'] == derivation['imageLayers']
                and config['config']['User'] == derivation['user']
                and config['config']['Entrypoint'] == derivation['entrypoint']
                and config['config']['Cmd'] == derivation['command']
                and config['config']['Labels']['io.bunkfy.fixture.recipe-sha256'] == policy['fixtureRecipeSha256'],
                'Raw derived-image configuration or filesystem proof changed.')
        require(runtime['imageConfigDigest'] == config_digest
                and runtime['containerUser'] == '10001:10001'
                and runtime['processUser'] == '10001' and runtime['dataOwner'] == '10001:10001',
                'The actual non-root derived fixture and writable volume must be exercised.')
    require(tool['name'] == 'Trivy' and tool['version'] == '0.76.1'
            and tool['archiveSha256'] == 'c7cbe3137fb8a48a17574bede3dd6beb16742d1e9772929940fc90082c39d9c0',
            'Use the reviewed scanner binary, not an unverified tool result.')
    root_component = sbom['metadata']['component']
    require(root_component['name'] == image and root_component['type'] == 'container', 'SBOM subject changed.')
    image_property = [value['value'] for value in root_component['properties']
                      if value['name'] == 'aquasecurity:trivy:ImageID']
    require(image_property == [metadata['ImageID']], 'SBOM image identity differs from the cryptographically bound scanner subject.')
    expected_purl = ('pkg:oci/silo@' + image_digest
                     + '?arch=amd64&repository_url=index.docker.io%2Fpgsty%2Fsilo')
    if derivation is None:
        require(root_component['purl'] == expected_purl, 'VEX must identify only this image digest and platform.')
    # A local build has no published registry-manifest digest. Bind its VEX
    # to its actual Docker configuration digest, which commits its rootfs IDs.
    product_id = expected_purl if derivation is None else 'urn:bunkfy:docker-config:' + config_digest
    product_hash = image_digest if derivation is None else config_digest
    targets = {value['Target']: value for value in scan['Results']}
    expected_targets = {image + ' (redhat 9.8)': 22, 'usr/bin/mcli': 108, 'usr/bin/silo': 264}
    require(set(targets) == set(expected_targets) and len(scan['Results']) == len(targets), 'Scanner targets disappeared or duplicated.')
    for name, minimum in expected_targets.items():
        require(len(targets[name].get('Packages', [])) >= minimum, 'Scanner package inventory is missing or reduced: ' + name)
    findings, parser_findings, finding_keys = [], [], set()
    for result in scan['Results']:
        require(not any(result.get(key) for key in ('Secrets', 'Misconfigurations', 'CustomResources')),
                'Unreviewed secret or configuration findings remain.')
        for finding in result.get('Vulnerabilities', []):
            match = (result['Target'] == policy['binaryTarget']
                     and finding['VulnerabilityID'] == policy['vulnerability']
                     and finding.get('PkgIdentifier', {}).get('PURL') == policy['packagePurl']
                     and finding['PkgName'] == 'github.com/minio/minio'
                     and finding['InstalledVersion'] == policy['packagePurl'].rsplit('@', 1)[1]
                     and finding['Severity'] == 'HIGH')
            key = (result['Target'], finding['VulnerabilityID'])
            require(key not in finding_keys, 'A raw vulnerability duplicated across a scanner target.')
            finding_keys.add(key)
            parser_match = (result['Target'] in parser_audit.SUBJECTS
                            and finding['VulnerabilityID'] == parser_audit.CVE
                            and finding.get('PkgIdentifier', {}).get('PURL') == parser_audit.PURL
                            and finding['PkgName'] == 'golang.org/x/net'
                            and finding['InstalledVersion'] == 'v0.61.0' and finding['Severity'] == 'HIGH')
            require(match or parser_match, 'A vulnerability outside the exact reviewed image/module/fix remains unresolved.')
            if parser_match:
                parser_audit.validate(binary_evidence, config_digest)
                parser_findings.append(finding)
            else:
                findings.append(finding)
    require(len(findings) <= 1 and len(parser_findings) <= 2, 'The reviewed findings duplicated across the scanner report.')
    require(scan_exit_code == (1 if findings or parser_findings else 0), 'Scanner exit status is missing or inconsistent with its raw report.')
    require(runtime['image'] == image
            and runtime['expectedSourceRevision'] == policy['sourceCommit']
            and runtime['actualSourceRevision'] == policy['sourceCommit']
            and runtime['result'] == 'passed', 'Actual runtime evidence does not qualify the reviewed image.')
    flags = {'containerRemoved', 'volumeRemoved', 'networkRemoved', 'generatedEnvironmentRemoved'}
    require(set(runtime['cleanup']) == flags
            and all(type(value) is bool and value for value in runtime['cleanup'].values()),
            'Storage cleanup is incomplete or unverified.')
    ordinary = {
        'readiness': 200, 'liveness': 200, 'anonymous account listing denied': 403,
        'create private bucket': 200, 'signed private object put': 200,
        'signed exact object read': 200, 'anonymous object read denied': 403,
        'invalid signature denied': 403, 'signed object head': 200, 'bounded range read': 206,
        'wrong entity tag rejected': 412, 'matching entity tag read': 200,
        'bounded prefix listing': 200, 'object survives provider restart': 200,
        'signed object deletion': 204, 'deleted object unavailable': 404, 'empty bucket deletion': 204,
    }
    expected_cases = Counter({name: 1 for name in ordinary})
    for label in ('CSV', 'JSON Lines', 'gzip CSV'):
        ordinary[label + ' probe object write'] = 200
        ordinary[label + ' service remains ready'] = 200
        ordinary[label + ' probe object deletion'] = 204
        expected_cases.update({label + ' probe object write': 2, label + ' service remains ready': 2,
                               label + ' probe object deletion': 1, label + ' positive Select control': 1,
                               label + ' oversized record rejected': 1})
    require(Counter(case['case'] for case in runtime['observations']) == expected_cases,
            'Storage or CVE binary controls are missing, duplicated or changed.')
    for case in runtime['observations']:
        name = case['case']
        if name in ordinary:
            require(case['actualStatus'] == case['expectedStatus'] == ordinary[name], 'A required S3 contract failed.')
        elif name.endswith('positive Select control'):
            require(case['httpStatus'] == 200 and case['errorCode'] is None, 'A positive Select control failed.')
        else:
            require(case['httpStatus'] in (200, 400) and case['errorCode'] == 'OverMaxRecordSize',
                    'A reviewed record-limit regression failed.')
    summary = {'schemaVersion': 1, 'result': 'passed', 'image': image,
               'imageConfigDigest': config_digest, 'platform': policy['platform'],
               'sourceCommit': policy['sourceCommit'], 'rawHighFindings': len(findings) + len(parser_findings),
               'fixedInReviewedFork': len(findings), 'parserCodeNotPresent': len(parser_findings), 'unresolvedSecurityFindings': 0,
               'runtimeObservations': len(runtime['observations']), 'reviewExpiresAt': policy['reviewExpiresAt'],
               'qualification': 'Exact engineering fixture only. Raw finding retained; no global ignore or production approval.'}
    vex = {'@context': 'https://openvex.dev/ns/v0.2.0',
           '@id': 'https://github.com/SadPossum/BunkFy/engineering-assessments/' + policy['sourceCommit'] + '/CVE-2026-39414',
           'author': 'Codex engineering continuation review for BunkFy',
           'role': 'Consumer assessment; not supplier-issued or a production approval',
           'timestamp': policy['reviewedAt'], 'version': 1,
           'statements': [{'vulnerability': {'name': policy['vulnerability']}, 'status': 'fixed',
                           'products': [{'@id': product_id,
                                         'hashes': {'sha-256': product_hash.removeprefix('sha256:')},
                                         'subcomponents': [{'@id': policy['packagePurl']}]}],
                           'status_notes': policy['assessment'] + ' Primary advisory: ' + policy['primaryAdvisory']
                                           + ' Supplier disclosure: ' + policy['supplierDisclosure']}]}
    if derivation is not None:
        summary['derivedFrom'] = {'image': policy['image'], 'imageConfigDigest': policy['imageConfigDigest'],
                                  'recipeSha256': policy['fixtureRecipeSha256']}
        summary['subjectKind'] = 'Local Docker configuration digest; no registry publication.'
        vex['@id'] += '/' + config_digest.removeprefix('sha256:')
        vex['statements'][0]['status_notes'] += (
            ' Derived fixture assessment: the verified base layers are unchanged; the reviewed recipe '
            + policy['fixtureRecipeSha256'] + ' only creates/chowns /data and selects UID/GID10001. '
            'Fresh raw scan, SBOM and all38 runtime controls bind the actual local Docker configuration '
            + config_digest + '. This is not a registry-manifest attestation or production approval.')
    if parser_findings:
        summary['binaryParserAssessment'] = binary_evidence
        vex['statements'].append({
            'vulnerability': {'name': parser_audit.CVE}, 'status': 'not_affected',
            'justification': 'vulnerable_code_not_present',
            'products': [{'@id': product_id, 'hashes': {'sha-256': product_hash.removeprefix('sha256:')},
                          'subcomponents': [{'@id': parser_audit.PURL}]}],
            'status_notes': ('Only the exact two reviewed binary SHA256s and this image configuration qualify. '
                             'The complete Go1.27.3 physical and inline function-name tables contain no '
                             'golang.org/x/net/html package code. x/net remains present for other packages. '
                             'All physical references and compiler positive/negative/inline controls were checked. '
                             'Raw findings are retained; no generic exception or reachability claim. '
                             + parser_audit.PRIMARY_ADVISORY + ' Linker contract: ' + parser_audit.LINKER_CONTRACT)})
    return summary, vex


def source_checks(policy):
    results = []
    require(set(policy['sourceFilesSha256']) == {
        'internal/s3select/csv/reader.go', 'internal/s3select/json/preader.go', 'internal/s3select/select.go'
    }, 'CSV, JSON Lines and CPU-independent dispatch source evidence are all required.')
    for path, expected in policy['sourceFilesSha256'].items():
        url = 'https://raw.githubusercontent.com/pgsty/silo/' + policy['sourceCommit'] + '/' + path
        with urllib.request.urlopen(url, timeout=30) as response:
            contents = response.read(256 * 1024)
        require(hashlib.sha256(contents).hexdigest() == expected, 'Reviewed immutable source bytes changed: ' + path)
        results.append({'path': path, 'sha256': expected})
    require(len(results) == 3, 'The CSV, JSON Lines and dispatch source evidence must all be checked.')
    return results


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, default=Path('artifacts'))
    parser.add_argument('--scanner-exit-code', type=int, choices=(0, 1), required=True)
    parser.add_argument('--derived-identity', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    policy = json.loads((root / '.gma/preview-storage-review.json').read_text())
    load = lambda name: json.loads((args.directory / name).read_text())
    derivation = None
    if args.derived_identity is not None:
        derivation = json.loads(args.derived_identity.read_text())
        recipe = root / 'eng/fixtures/minio/Silo.TestFixture.Dockerfile'
        require(hashlib.sha256(recipe.read_bytes()).hexdigest() == policy['fixtureRecipeSha256'],
                'The checked-out fixture recipe differs from the reviewed recipe.')
    scan = load('silo-security.json')
    binary_evidence = None
    if any(finding.get('VulnerabilityID') == parser_audit.CVE
           for result in scan.get('Results', []) for finding in result.get('Vulnerabilities', [])):
        image, image_id = ((policy['image'], policy['imageConfigDigest']) if derivation is None
                           else derived_subject(policy, derivation))
        binary_evidence = parser_audit.collect(image_id, image_reference=image)
        (args.directory / 'silo-parser-applicability.json').write_text(json.dumps(binary_evidence, indent=2) + '\n')
    summary, vex = verify(policy, scan, load('silo-runtime.json'), load('silo-sbom.cdx.json'),
                          load('tool-identity.json'), args.scanner_exit_code,
                          derivation=derivation, binary_evidence=binary_evidence)
    summary['reviewedSourceChecks'] = source_checks(policy)
    (args.directory / 'silo-security-summary.json').write_text(json.dumps(summary, indent=2) + '\n')
    (args.directory / 'silo-fixed-fork.vex.json').write_text(json.dumps(vex, indent=2) + '\n')
    print(json.dumps(summary, indent=2))

