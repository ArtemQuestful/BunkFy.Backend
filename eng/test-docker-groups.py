#!/usr/bin/env python3
"""Partition every solution test project into bounded provider jobs without dropping tests."""

import argparse
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import tempfile
import uuid
import xml.etree.ElementTree as ET


GROUPS = ('framework-extensions', 'gma-access-administration', 'gma-auth-files',
          'gma-services', 'product-core', 'product-services', 'host-architecture', 'product-integration')

# Minimums from the qualified provider suite. Review intentional coverage reductions.
MINIMUM_NATIVE_CASES = {
    'framework-extensions': 4, 'gma-access-administration': 29, 'gma-auth-files': 5,
    'gma-services': 56, 'product-core': 1, 'product-services': 0,
    'host-architecture': 0, 'product-integration': 214,
}


def verify_native_evidence(name, reports):
    if not reports or len(set(reports)) != len(reports):
        raise RuntimeError('Native test reports must have unique output paths.')
    passed = 0
    for report in reports:
        try:
            counters = ET.parse(report).findall('.//{*}ResultSummary/{*}Counters')
            if len(counters) != 1:
                raise ValueError('Expected one test result summary.')
            values = {key: int(value) for key, value in counters[0].attrib.items()}
            required = ('total', 'executed', 'passed', 'failed', 'notExecuted')
            if any(key not in values for key in required) or any(value < 0 for value in values.values()):
                raise ValueError('Test result counters are missing or invalid.')
        except (OSError, ET.ParseError, ValueError) as error:
            raise RuntimeError(f'Native test report is missing or invalid: {report}') from error
        unsuccessful = ('failed', 'notExecuted', 'error', 'timeout', 'aborted', 'inconclusive',
                        'notRunnable', 'disconnected', 'inProgress', 'pending', 'warning', 'passedButRunAborted')
        if (values['total'] != values['executed'] or values['executed'] != values['passed'] or
                any(values.get(key, 0) for key in unsuccessful)):
            raise RuntimeError(f'Native test report contains skipped, failed or incomplete cases: {report}')
        passed += values['passed']
    minimum = MINIMUM_NATIVE_CASES[name]
    if passed < minimum:
        raise RuntimeError(f'Native group {name} executed {passed} cases; expected at least {minimum}.')
    return {'group': name, 'passed': passed, 'minimumNativeCases': minimum, 'reports': len(reports)}


def group_for(project):
    if project.startswith(('gma/framework/', 'gma/extensions/')):
        return 'framework-extensions'
    if project.startswith(('gma/modules/access-control/', 'gma/modules/administration/')):
        return 'gma-access-administration'
    if project.startswith(('gma/modules/auth/', 'gma/modules/files/')):
        return 'gma-auth-files'
    if project.startswith('gma/'):
        return 'gma-services'
    if project == 'tests/Integration.Tests/Integration.Tests.csproj':
        return 'product-integration'
    if project.startswith('tests/'):
        return 'host-architecture'
    if any('/BunkFy.Modules.' + name + '.Tests/' in project for name in
           ('Guests', 'Inventory', 'Properties', 'Reservations', 'Staff', 'Stations', 'Workspaces')):
        return 'product-core'
    return 'product-services'


def partition(solution):
    groups = {name: [] for name in GROUPS}
    seen = set()
    for element in ET.parse(solution).iter('Project'):
        project = element.attrib['Path'].replace('\\', '/')
        path = PurePosixPath(project)
        if path.is_absolute() or '..' in path.parts or ':' in project:
            raise ValueError('Solution project must remain inside the source checkout.')
        if '/tests/' not in '/' + project:
            if path.name.endswith(('Tests.csproj', 'Specs.csproj')):
                raise ValueError('Test project must use the repository test-directory convention.')
            continue
        if not project.endswith('.csproj') or project in seen:
            raise ValueError('Ambiguous or duplicate test project in solution.')
        seen.add(project)
        groups[group_for(project)].append(project)
    if not seen:
        raise ValueError('No solution test projects discovered; refusing an empty provider run.')
    return {name: sorted(projects) for name, projects in groups.items() if projects}


def run_group(repository, name, projects):
    # Build only this job's dependency graph. A temporary solution does not change checked-in pins.
    with tempfile.TemporaryDirectory(prefix='bunkfy-provider-', dir=os.environ.get('RUNNER_TEMP')) as owned:
        solution = Path(owned) / 'ProviderTests.slnx'
        document = ET.Element('Solution')
        for project in projects:
            source = repository / project
            if not source.is_file():
                raise FileNotFoundError(source)
            ET.SubElement(document, 'Project', Path=str(source.resolve()))
        ET.ElementTree(document).write(solution, encoding='utf-8', xml_declaration=True)
        results = repository / 'artifacts/test-results/docker' / name
        results.mkdir(parents=True, exist_ok=True)
        print(json.dumps({'group': name, 'projects': projects}), flush=True)
        if name == 'product-integration':
            subprocess.run(['pwsh', '-NoProfile', '-File', str(repository / 'eng/build-provider-images.ps1')],
                           cwd=repository, check=True)
        subprocess.run(['dotnet', 'restore', str(solution)], cwd=repository, check=True)
        subprocess.run(['dotnet', 'build', str(solution), '--no-restore', '-m:1', '-nr:false'],
                       cwd=repository, check=True)
        environment = os.environ.copy()
        environment['GMA_REQUIRE_DOCKER_TESTS'] = 'true'
        if name == 'product-integration':
            environment['BFX_CMP_VARIANT'] = 'candidate'
            environment['BFX_CMP_EVIDENCE_DIR'] = str(results / 'ordinary' / uuid.uuid4().hex / 'candidate')
            source_head = subprocess.run(
                ['git', 'rev-parse', 'HEAD'], cwd=repository, check=True,
                capture_output=True, text=True).stdout.strip()
            source_changes = subprocess.run(['git', 'status', '--porcelain'], cwd=repository,
                                            check=True, capture_output=True, text=True).stdout.strip()
            environment['BFX_CMP_BUILD_FINGERPRINT'] = (
                'working-tree-based-on:' + source_head if source_changes else source_head)
        failed = []
        reports = []
        for project in projects:
            report = results / (Path(project).stem + '.trx')
            report.unlink(missing_ok=True)
            reports.append(report)
            result = subprocess.run(
                ['dotnet', 'test', project, '--no-build', '--filter', 'Category=Docker',
                 '--logger', 'console;verbosity=minimal',
                 '--logger', 'trx;LogFileName=' + Path(project).stem + '.trx',
                 '--results-directory', str(results), '-m:1', '-nr:false'],
                cwd=repository, env=environment, check=False)
            if result.returncode:
                failed.append(project)
        if failed:
            raise RuntimeError('Provider test projects failed: ' + ', '.join(failed))
        print(json.dumps(verify_native_evidence(name, reports)), flush=True)



if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    selection = parser.add_mutually_exclusive_group(required=True)
    selection.add_argument('--matrix', action='store_true')
    selection.add_argument('--group', choices=GROUPS)
    options = parser.parse_args()
    repository = Path(__file__).resolve().parent.parent
    groups = partition(repository / 'BunkFy.slnx')
    if options.matrix:
        print(json.dumps({'group': list(groups)}))
    elif options.group not in groups:
        raise ValueError('Selected provider group has no projects.')
    else:
        run_group(repository, options.group, groups[options.group])
