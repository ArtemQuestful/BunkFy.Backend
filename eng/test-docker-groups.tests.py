import importlib.util
import sys
sys.dont_write_bytecode = True
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('provider_groups', Path(__file__).with_name('test-docker-groups.py'))
groups = importlib.util.module_from_spec(spec)
spec.loader.exec_module(groups)


class ProviderGroupingTests(unittest.TestCase):
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
