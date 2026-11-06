import copy
import importlib.util
import json
from pathlib import Path
import sys
import unittest

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('comparison', Path(__file__).with_name('compare-ordinary-behavior.py'))
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)


def document(variant):
    return {'evidenceId': 'BFX-SS-K0-CMP-0001', 'variant': variant, 'buildFingerprint': variant + '-source',
            'denominator': {'operations': 26, 'candidateControlObservations': 52, 'comparisons': 26},
            'normalizationRules': ['Only producer-declared field paths are normalized.'],
            'normalizationPathAllowlist': {'tokenPaths': ['$.accessToken']},
            'observations': [{'id': name, 'ordinal': index, 'expectedStatus': 200, 'actualStatus': 200,
                              'result': 'pass', 'headers': {'cache-control': ['no-store']},
                              'runtime': {'effectiveAuthorizationResultHandlerCount': 1}}
                             for index, name in enumerate(comparison.OPERATION_IDS, 1)]}


class OrdinaryComparisonSafetyTests(unittest.TestCase):
    def compare(self, control, candidate):
        return comparison.compare(control, candidate, 'control-source', 'candidate-source')

    def test_compares_all_52_observations_while_preserving_distinct_source_provenance(self):
        result = self.compare(document('control'), document('candidate'))
        self.assertTrue(result['passed'])
        self.assertEqual(52, result['observations'])
        self.assertEqual(26, result['comparisons'])

    def test_a_single_header_change_is_a_failure_with_its_exact_path(self):
        candidate = document('candidate')
        candidate['observations'][19]['headers']['cache-control'] = ['public']
        result = self.compare(document('control'), candidate)
        self.assertFalse(result['passed'])
        self.assertEqual(['$.observations[19].headers.cache-control[0]'], result['differentPaths'])

    def test_missing_duplicate_empty_and_unsuccessful_operations_fail_closed(self):
        for mode in ('missing', 'duplicate', 'empty', 'failed', 'multiple-handlers'):
            control, candidate = document('control'), document('candidate')
            for item in (control, candidate):
                if mode == 'missing': item['observations'].pop()
                elif mode == 'duplicate': item['observations'][1] = copy.deepcopy(item['observations'][0])
                elif mode == 'empty': item['observations'] = []
                elif mode == 'failed': item['observations'][0]['result'] = 'fail'
                else: item['observations'][0]['runtime']['effectiveAuthorizationResultHandlerCount'] = 2
            with self.subTest(mode=mode), self.assertRaises(ValueError):
                self.compare(control, candidate)

    def test_changed_redaction_rules_are_compared_instead_of_silently_adopted(self):
        candidate = document('candidate')
        candidate['normalizationPathAllowlist']['tokenPaths'].append('$.*')
        self.assertFalse(self.compare(document('control'), candidate)['passed'])

    def test_wrong_source_and_mislabeled_control_are_rejected(self):
        for field, value in [('buildFingerprint', 'other-source'), ('variant', 'candidate')]:
            control = document('control')
            control[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.compare(control, document('candidate'))

    def test_duplicate_json_fields_are_rejected(self):
        with self.assertRaises(ValueError):
            json.loads('{"variant":"control","variant":"candidate"}', object_pairs_hook=comparison.unique_object)


if __name__ == '__main__':
    unittest.main()
