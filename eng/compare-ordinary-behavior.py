#!/usr/bin/env python3
"""Compare the existing 26-operation canonical evidence without further normalization."""

import argparse
import json
from pathlib import Path
import sys

EVIDENCE_ID = 'BFX-SS-K0-CMP-0001'
OPERATION_IDS = (
    'CMP-001-register-success', 'CMP-002-register-conflict',
    'CMP-003-login-success', 'CMP-004-login-invalid', 'CMP-005-login-rate-limited',
    'CMP-006-mfa-login-challenge', 'CMP-007-mfa-complete',
    'CMP-008-refresh-success', 'CMP-009-refresh-invalid',
    'CMP-010-step-up-success', 'CMP-011-step-up-failure', 'CMP-012-sign-out',
    'CMP-013-sessions-list', 'CMP-014-session-revoke',
    'CMP-015-password-recovery-request', 'CMP-016-password-recovery-complete',
    'CMP-017-external-exchange-sign-in', 'CMP-018-external-exchange-link',
    'CMP-019-access-permission-evaluate', 'CMP-020-tenant-read-permitted',
    'CMP-021-tenant-read-forbidden', 'CMP-022-admin-read-permitted',
    'CMP-023-admin-read-forbidden', 'CMP-024-admin-read-unauthenticated',
    'CMP-025-signalr-ordinary-bearer', 'CMP-026-sse-ordinary-bearer-delivery',
)
DOCUMENT_KEYS = {'evidenceId', 'variant', 'buildFingerprint', 'denominator',
                 'normalizationRules', 'normalizationPathAllowlist', 'observations'}


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('Duplicate JSON evidence field.')
        result[key] = value
    return result


def load_evidence(directory, variant):
    root = Path(directory).resolve()
    files = list(root.rglob(f'{EVIDENCE_ID}-{variant}.canonical.json'))
    if len(files) != 1:
        raise ValueError(f'Require exactly one {variant} canonical document.')
    source = files[0].resolve()
    if not source.is_relative_to(root) or source.stat().st_size > 8 * 1024 * 1024:
        raise ValueError('Evidence must be a bounded file inside its artifact directory.')
    return json.loads(source.read_text(encoding='utf-8'), object_pairs_hook=unique_object)


def validate(document, variant, fingerprint):
    if not fingerprint or set(document) != DOCUMENT_KEYS:
        raise ValueError('Missing source fingerprint or changed evidence document schema.')
    if (document['evidenceId'] != EVIDENCE_ID or document['variant'] != variant
            or document['buildFingerprint'] != fingerprint):
        raise ValueError(f'{variant} evidence does not match its declared source and variant.')
    if document['denominator'] != {'operations': 26, 'candidateControlObservations': 52, 'comparisons': 26}:
        raise ValueError('The frozen comparison denominator changed.')
    observations = document['observations']
    if len(observations) != 26 or tuple(item['id'] for item in observations) != OPERATION_IDS:
        raise ValueError('Missing, duplicate, reordered or unexpected ordinary operations.')
    if not document['normalizationRules'] or not isinstance(document['normalizationPathAllowlist'], dict):
        raise ValueError('Normalization provenance is required.')
    for ordinal, item in enumerate(observations, 1):
        if (type(item['ordinal']) is not int or item['ordinal'] != ordinal
                or type(item['expectedStatus']) is not int
                or item['actualStatus'] != item['expectedStatus'] or item['result'] != 'pass'
                or not isinstance(item['runtime'], dict)
                or item['runtime'].get('effectiveAuthorizationResultHandlerCount') != 1):
            raise ValueError(f'{variant} ordinary operation {ordinal} did not pass its own runtime contract.')


def differences(left, right, path='$'):
    if type(left) is not type(right):
        return [path]
    if isinstance(left, dict):
        result = []
        for key in sorted(set(left) | set(right)):
            result.extend([path + '.' + key] if key not in left or key not in right
                          else differences(left[key], right[key], path + '.' + key))
            if len(result) >= 100:
                break
        return result[:100]
    if isinstance(left, list):
        if len(left) != len(right):
            return [path + '.length']
        result = []
        for index, (a, b) in enumerate(zip(left, right)):
            result.extend(differences(a, b, f'{path}[{index}]'))
            if len(result) >= 100:
                break
        return result[:100]
    return [] if left == right else [path]


def compare(control, candidate, control_fingerprint, candidate_fingerprint):
    validate(control, 'control', control_fingerprint)
    validate(candidate, 'candidate', candidate_fingerprint)
    # Only these two provenance fields necessarily differ. Bodies, headers,
    # schemas, logs, runtime, subresponses and the producer allowlist stay exact.
    comparable = lambda value: {key: item for key, item in value.items()
                                if key not in ('variant', 'buildFingerprint')}
    changed = differences(comparable(control), comparable(candidate))
    return {'evidenceId': EVIDENCE_ID, 'passed': not changed, 'operations': 26,
            'observations': 52, 'comparisons': 26, 'controlFingerprint': control_fingerprint,
            'candidateFingerprint': candidate_fingerprint, 'differentPaths': changed}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--control-directory', required=True)
    parser.add_argument('--candidate-directory', required=True)
    parser.add_argument('--control-fingerprint', required=True)
    parser.add_argument('--candidate-fingerprint', required=True)
    parser.add_argument('--output', required=True)
    options = parser.parse_args()
    try:
        report = compare(load_evidence(options.control_directory, 'control'),
                         load_evidence(options.candidate_directory, 'candidate'),
                         options.control_fingerprint, options.candidate_fingerprint)
        destination = Path(options.output)
        destination.parent.mkdir(parents=True, exist_ok=True)
        with destination.open('x', encoding='utf-8') as output:
            json.dump(report, output, indent=2)
            output.write('\n')
        print(json.dumps(report))
        sys.exit(0 if report['passed'] else 1)
    except (ValueError, KeyError, TypeError, OSError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
