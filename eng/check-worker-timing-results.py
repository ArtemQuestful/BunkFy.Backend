#!/usr/bin/env python3
"""Require all unchanged isolated worker contracts; empty, skipped or duplicated passes fail."""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET

WORKER_CLASS = 'Gma.Framework.Tests.Tasks.TaskWorkerServiceTests'
REQUIRED_METHODS = {
    'Worker_claims_only_available_execution_capacity',
    'Worker_rotates_across_groups_before_waiting_for_running_work',
    'Worker_automatically_heartbeats_a_running_handler',
    'Worker_uses_registration_timeout_before_global_fallback',
    'Worker_uses_global_timeout_when_registration_has_no_override',
    'Worker_cancels_and_fails_a_handler_when_automatic_heartbeat_fails',
    'Worker_does_not_persist_handler_exception_messages',
    'Worker_does_not_retry_terminal_handler_failures',
    'Worker_does_not_persist_context_contributor_failure_text',
    'Worker_does_not_invoke_handler_after_start_loses_the_lease',
    'Worker_does_not_count_a_terminal_transition_rejected_after_handler_completion',
}
REQUIRED_CASES = {WORKER_CLASS + '.' + method for method in REQUIRED_METHODS}

def verify(directory):
    reports = sorted(Path(directory).rglob('*.trx'))
    if not reports:
        raise ValueError('No isolated worker TRX reports were produced.')
    seen = set()
    executions = set()
    for report in reports:
        if report.is_symlink() or report.stat().st_size > 16 * 1024 * 1024:
            raise ValueError('Invalid isolated report file.')
        root = ET.parse(report).getroot()
        definitions = {}
        for item in root.findall('.//{*}TestDefinitions/{*}UnitTest'):
            identity = item.attrib['id']
            if identity in definitions:
                raise ValueError('Duplicate test definition.')
            assembly = item.attrib['storage'].replace('\\', '/').rsplit('/', 1)[-1].lower()
            definitions[identity] = (assembly, item.attrib['name'])
        results = root.findall('.//{*}Results/{*}UnitTestResult')
        for result in results:
            assembly, name = definitions[result.attrib['testId']]
            execution = result.attrib['executionId']
            if (assembly != 'gma.framework.tests.dll' or name not in REQUIRED_CASES
                    or result.attrib['testName'] != name or name in seen or execution in executions
                    or result.attrib['outcome'] != 'Passed'):
                raise ValueError('Worker case failed, skipped, duplicated or has an unexpected identity.')
            seen.add(name)
            executions.add(execution)
        counters = root.findall('.//{*}ResultSummary/{*}Counters')
        if len(counters) != 1:
            raise ValueError('Expected one report summary.')
        values = {key: int(value) for key, value in counters[0].attrib.items()}
        if (not results or any(values.get(key) != len(results) for key in ('total', 'executed', 'passed'))
                or any(values.get(key, -1) != 0 for key in ('failed', 'notExecuted'))
                or any(value for key, value in values.items() if key not in ('total', 'executed', 'passed', 'completed'))):
            raise ValueError('Worker report counters do not describe completed passes.')
    if seen != REQUIRED_CASES:
        raise ValueError('One or more of the eleven required worker contracts is missing.')
    return {'reports': len(reports), 'passed': len(seen), 'required': len(REQUIRED_CASES)}

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    print(json.dumps(verify(parser.parse_args().directory)))
