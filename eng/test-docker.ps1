param(
    [switch] $NoBuild,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $DotNetArguments
)

. (Join-Path $PSScriptRoot 'common.ps1')

$previousRequireDockerTests = $env:GMA_REQUIRE_DOCKER_TESTS
$env:GMA_REQUIRE_DOCKER_TESTS = 'true'
$comparisonEnvironmentNames = @('BFX_CMP_VARIANT', 'BFX_CMP_EVIDENCE_DIR', 'BFX_CMP_BUILD_FINGERPRINT')
$previousComparisonEnvironment = @{}
foreach ($name in $comparisonEnvironmentNames) {
    $previousComparisonEnvironment[$name] = [System.Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    $configuredComparisonNames = @($comparisonEnvironmentNames | Where-Object {
        -not [string]::IsNullOrWhiteSpace($previousComparisonEnvironment[$_])
    })
    if ($configuredComparisonNames.Count -ne 0 -and $configuredComparisonNames.Count -ne 3) {
        throw 'Set all three BFX_CMP_* comparison settings, or leave all unset for an owned candidate observation.'
    }
    if ($configuredComparisonNames.Count -eq 0) {
        $sourceHead = (& git -C (Get-GmaRepositoryRoot) rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the candidate source.' }
        $sourceChanges = @(& git -C (Get-GmaRepositoryRoot) status --porcelain)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the candidate working tree.' }
        $env:BFX_CMP_VARIANT = 'candidate'
        $env:BFX_CMP_BUILD_FINGERPRINT = if ($sourceChanges.Count -eq 0) { $sourceHead } else { "working-tree-based-on:$sourceHead" }
        $env:BFX_CMP_EVIDENCE_DIR = Join-GmaPath "artifacts/test-results/docker/manual/$([Guid]::NewGuid().ToString('N'))/candidate"
    }
    & (Join-Path $PSScriptRoot 'build-provider-images.ps1')
    $arguments = @(
        'test',
        (Join-GmaPath 'tests\Integration.Tests\Integration.Tests.csproj'),
        '--filter',
        'Category=Docker',
        '--logger',
        'console;verbosity=minimal'
    )

    if ($NoBuild) {
        $arguments += '--no-build'
    }

    $arguments += $DotNetArguments | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    Invoke-GmaDotNet -Arguments $arguments
}
finally {
    $env:GMA_REQUIRE_DOCKER_TESTS = $previousRequireDockerTests
    foreach ($name in $comparisonEnvironmentNames) {
        [System.Environment]::SetEnvironmentVariable($name, $previousComparisonEnvironment[$name], 'Process')
    }
}
