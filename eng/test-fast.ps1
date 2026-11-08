param(
    [switch] $NoBuild,

    [switch] $IsolateWorkerTimingContracts = $IsWindows,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $DotNetArguments
)

. (Join-Path $PSScriptRoot 'common.ps1')

$solutionPath = Join-GmaPath 'BunkFy.slnx'
[xml] $solution = Get-Content -LiteralPath $solutionPath -Raw
$testProjects = @(
    $solution.SelectNodes('/Solution//Project[@Path]') |
        ForEach-Object {
            $projectPath = Join-GmaPath $_.Path
            [xml] $project = Get-Content -LiteralPath $projectPath -Raw
            $isTestProject = $project.SelectSingleNode(
                '/Project/PropertyGroup/IsTestProject[text()="true"]')
            if ($null -ne $isTestProject) {
                $projectPath
            }
        }
)

if ($testProjects.Count -eq 0) {
    throw "No test projects were found in '$solutionPath'."
}

$workerClass = 'Gma.Framework.Tests.Tasks.TaskWorkerServiceTests.'
foreach ($testProject in $testProjects) {
    $isWorkerAssembly = [IO.Path]::GetFileNameWithoutExtension($testProject) -eq 'Gma.Framework.Tests'
    $filter = 'Category!=Docker'
    if ($IsolateWorkerTimingContracts -and $isWorkerAssembly) {
        $filter += "&FullyQualifiedName!~$workerClass"
    }
    $arguments = @(
        'test',
        $testProject,
        '--filter',
        $filter,
        '--logger',
        'console;verbosity=minimal'
    )

    if ($NoBuild) {
        $arguments += '--no-build'
    }

    $arguments += $DotNetArguments | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    if ($IsolateWorkerTimingContracts -and $isWorkerAssembly) {
        if ('--' -notin $arguments) { $arguments += '--' }
        $arguments += 'xUnit.ParallelizeTestCollections=false'
    }
    Invoke-GmaDotNet -Arguments $arguments
    if ($IsolateWorkerTimingContracts -and $isWorkerAssembly) {
        $results = Join-GmaPath ('artifacts/test-results/worker-timing/' + [Guid]::NewGuid().ToString('N'))
        $isolated = @('test', $testProject, '--filter', "FullyQualifiedName~$workerClass",
            '--logger', 'console;verbosity=minimal', '--logger', 'trx', '--results-directory', $results,
            '-m:1', '-nr:false')
        if ($NoBuild) { $isolated += '--no-build' }
        $isolated += @('--', 'xUnit.ParallelizeTestCollections=false')
        Invoke-GmaDotNet -Arguments $isolated
        & python3 (Join-GmaPath 'eng/check-worker-timing-results.py') $results
        if ($LASTEXITCODE -ne 0) { throw 'Required isolated worker timing evidence did not pass.' }
    }
}
