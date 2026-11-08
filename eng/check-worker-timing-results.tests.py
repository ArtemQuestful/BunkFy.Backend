import importlib.util
from pathlib import Path
import tempfile
import unittest
import uuid
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('worker_results', Path(__file__).with_name('check-worker-timing-results.py'))
evidence = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evidence)

class WorkerEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = ET.Element('TestRun')
        definitions = ET.SubElement(self.root, 'TestDefinitions')
        results = ET.SubElement(self.root, 'Results')
        for name in sorted(evidence.REQUIRED_CASES):
            identity = str(uuid.uuid4())
            ET.SubElement(definitions, 'UnitTest', id=identity, name=name, storage='Gma.Framework.Tests.dll')
            ET.SubElement(results, 'UnitTestResult', testId=identity, testName=name, outcome='Passed', executionId=str(uuid.uuid4()))
        self.counters = ET.SubElement(ET.SubElement(self.root, 'ResultSummary'), 'Counters', total='11', executed='11', passed='11', failed='0', notExecuted='0')

    def check(self):
        ET.ElementTree(self.root).write(Path(self.directory.name) / 'worker.trx')
        return evidence.verify(self.directory.name)

    def test_guard_lists_every_framework_worker_contract(self):
        import re
        source=Path(__file__).resolve().parents[1] / 'gma/framework/tests/Gma.Framework.Tests/Tasks/TaskWorkerServiceTests.cs'
        declared=set(re.findall(r'\[Fact\]\s+public async Task (\w+)\(', source.read_text()))
        self.assertEqual(declared, evidence.REQUIRED_METHODS)

    def test_all_required_cases_pass(self):
        self.assertEqual(self.check()['passed'], 11)

    def test_absent_reports_fail(self):
        with self.assertRaises(ValueError): evidence.verify(self.directory.name)

    def test_missing_case_fails_even_with_consistent_lower_counters(self):
        results = self.root.find('Results');results.remove(results[-1])
        for key in ('total', 'executed', 'passed'):self.counters.set(key, '10')
        with self.assertRaises(ValueError):self.check()

    def test_skipped_failed_and_incomplete_cases_fail(self):
        for outcome in ('NotExecuted', 'Failed', 'InProgress', 'Aborted'):
            self.root.find('Results')[0].set('outcome', outcome)
            with self.subTest(outcome=outcome), self.assertRaises(ValueError):self.check()

    def test_duplicate_case_fails(self):
        results=self.root.find('Results');results.append(ET.fromstring(ET.tostring(results[0])))
        with self.assertRaises(ValueError):self.check()

    def test_unrelated_case_cannot_replace_a_required_case(self):
        self.root.find('TestDefinitions')[0].set('name', 'Other.Tests.Unrelated')
        self.root.find('Results')[0].set('testName', 'Other.Tests.Unrelated')
        with self.assertRaises(ValueError):self.check()

    def test_inflated_summary_fails(self):
        self.counters.set('passed', '12')
        with self.assertRaises(ValueError):self.check()

if __name__ == '__main__':unittest.main()
