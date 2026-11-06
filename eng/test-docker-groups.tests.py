import importlib.util
import contextlib
import io
import sys
sys.dont_write_bytecode = True
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('provider_groups', Path(__file__).with_name('test-docker-groups.py'))
groups = importlib.util.module_from_spec(spec)
spec.loader.exec_module(groups)

REDIS_CONTRACT = ('Gma.Framework.Tests.RateLimiting.RedisProviderContractTests.'
                  'Resp2_and_default_resp3_preserve_cache_isolation_atomic_limits_and_outage_contracts')


class ProviderGroupingTests(unittest.TestCase):
    def run_fixture(self, passed=5, skipped=0, failed=0, mode='valid', stale=False,
                    group='framework-extensions', process_exit=0,
                    required_results=((REDIS_CONTRACT, 'Passed'),), require_strict_build=False):
        with tempfile.TemporaryDirectory(prefix='native-evidence-test-') as directory:
            repository = Path(directory)
            project = 'gma/framework/tests/Framework.Tests/Framework.Tests.csproj'
            source = repository / project
            source.parent.mkdir(parents=True)
            source.write_text('<Project Sdk="Microsoft.NET.Sdk" />')
            results = repository / 'artifacts/test-results/docker' / group
            report = results / 'Framework.Tests.trx'
            results.mkdir(parents=True)
            if stale:
                report.write_text('<TestRun><ResultSummary><Counters total="4" executed="4" '
                                  'passed="4" failed="0" notExecuted="0" /></ResultSummary></TestRun>')
            calls = []

            def invoke(arguments, **kwargs):
                self.assertEqual('dotnet', arguments[0])
                calls.append(arguments[1])
                if arguments[1] == 'build' and require_strict_build:
                    self.assertIn('-warnaserror:MSB3277', arguments)
                if arguments[1] != 'test':
                    return SimpleNamespace(returncode=0)
                self.assertEqual('true', kwargs['env']['GMA_REQUIRE_DOCKER_TESTS'])
                if mode == 'valid':
                    root = ET.Element('TestRun', xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
                    results_node = ET.SubElement(root, 'Results')
                    named_results = list(required_results) if group == 'framework-extensions' and passed else []
                    named_results.extend((f'Other.Provider.Case{index}', 'Passed')
                                         for index in range(max(0, passed - len(named_results))))
                    for name, outcome in named_results:
                        ET.SubElement(results_node, 'UnitTestResult', testName=name, outcome=outcome)
                    summary = ET.SubElement(root, 'ResultSummary')
                    ET.SubElement(summary, 'Counters', total=str(passed + skipped + failed),
                                  executed=str(passed + failed), passed=str(passed),
                                  failed=str(failed), notExecuted=str(skipped))
                    ET.ElementTree(root).write(report)
                elif mode == 'malformed':
                    report.write_text('<TestRun>')
                elif mode != 'missing':
                    raise AssertionError('Unknown fixture mode.')
                return SimpleNamespace(returncode=process_exit)

            with patch.object(groups.subprocess, 'run', side_effect=invoke), contextlib.redirect_stdout(io.StringIO()):
                groups.run_group(repository, group, [project])
            return calls

    def test_success_requires_fresh_native_case_evidence(self):
        self.assertEqual(['restore', 'build', 'test'], self.run_fixture())

    def test_zero_or_reduced_native_discovery_fails_even_with_successful_processes(self):
        for count in (0, 3):
            with self.subTest(count=count), self.assertRaisesRegex(RuntimeError, 'expected at least'):
                self.run_fixture(passed=count)

    def test_skipped_or_failed_cases_cannot_be_hidden_by_a_successful_process(self):
        for skipped, failed in ((1, 0), (0, 1)):
            with self.subTest(skipped=skipped, failed=failed), self.assertRaisesRegex(RuntimeError, 'skipped, failed'):
                self.run_fixture(skipped=skipped, failed=failed)

    def test_missing_malformed_or_stale_reports_fail_closed(self):
        for mode, stale in (('missing', False), ('malformed', False), ('missing', True)):
            with self.subTest(mode=mode, stale=stale), self.assertRaisesRegex(RuntimeError, 'missing or invalid'):
                self.run_fixture(mode=mode, stale=stale)

    def test_legitimately_empty_groups_still_require_valid_reports(self):
        self.assertEqual(['restore', 'build', 'test'], self.run_fixture(passed=0, group='host-architecture'))
        with self.assertRaisesRegex(RuntimeError, 'missing or invalid'):
            self.run_fixture(passed=0, group='host-architecture', mode='missing')

    def test_a_failed_test_process_still_fails_with_a_passing_report(self):
        with self.assertRaisesRegex(RuntimeError, 'Provider test projects failed'):
            self.run_fixture(process_exit=1)

    def test_four_old_cases_cannot_satisfy_the_adopted_five_case_minimum(self):
        with self.assertRaisesRegex(RuntimeError, 'expected at least'):
            self.run_fixture(passed=4)

    def test_dependency_assembly_conflicts_fail_the_provider_build(self):
        self.run_fixture(require_strict_build=True)

    def test_five_other_cases_cannot_replace_the_required_redis_contract(self):
        with self.assertRaisesRegex(RuntimeError, 'Required native case'):
            self.run_fixture(required_results=(('Other.Provider.Case', 'Passed'),))

    def test_required_redis_contract_must_not_be_duplicated_or_unsuccessful(self):
        for results in [((REDIS_CONTRACT, 'Passed'), (REDIS_CONTRACT, 'Passed')),
                        ((REDIS_CONTRACT, 'Failed'),), ((REDIS_CONTRACT, 'NotExecuted'),)]:
            with self.subTest(results=results), self.assertRaisesRegex(RuntimeError, 'Required native case'):
                self.run_fixture(required_results=results)

    def partition(self, paths):
        with tempfile.TemporaryDirectory(prefix='provider-plan-test-') as directory:
            solution = Path(directory) / 'Fixture.slnx'
            document = ET.Element('Solution')
            for path in paths:
                ET.SubElement(document, 'Project', Path=path)
            ET.ElementTree(document).write(solution)
            return groups.partition(solution)

    def test_every_test_project_is_retained_once_including_future_modules(self):
        expected = {
            'gma/framework/tests/Framework.Tests/Framework.Tests.csproj',
            'gma/modules/auth/tests/Auth.IntegrationTests/Auth.IntegrationTests.csproj',
            'tests/Architecture.Tests/Architecture.Tests.csproj',
            'src/Modules/Inventory/tests/BunkFy.Modules.Inventory.Tests/BunkFy.Modules.Inventory.Tests.csproj',
            'src/Modules/NewModule/tests/NewModule.Tests/NewModule.Tests.csproj',
        }
        result = self.partition(sorted(expected) + ['src/Host/Host.csproj'])
        actual = [path for projects in result.values() for path in projects]
        self.assertEqual(expected, set(actual))
        self.assertEqual(len(expected), len(actual))
        self.assertIn('src/Modules/NewModule/tests/NewModule.Tests/NewModule.Tests.csproj', result['product-services'])

    def test_empty_duplicate_and_escaping_solutions_fail_closed(self):
        for paths in [[], ['src/Host.csproj'], ['tests/Test.csproj', 'tests/Test.csproj'],
                      ['../tests/Test.csproj'], ['/tests/Test.csproj'], ['C:/tests/Test.csproj'], ['src/Bad.Tests.csproj']]:
            with self.subTest(paths=paths):
                with self.assertRaises(ValueError):
                    self.partition(paths)

    def test_windows_paths_are_normalized_without_changing_group_membership(self):
        result = self.partition(['tests\\Architecture.Tests\\Architecture.Tests.csproj'])
        self.assertEqual(['host-architecture'], list(result))
        self.assertEqual(['tests/Architecture.Tests/Architecture.Tests.csproj'], result['host-architecture'])


if __name__ == '__main__':
    unittest.main()
