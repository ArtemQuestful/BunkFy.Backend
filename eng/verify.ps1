param(
    [switch] $SkipRestore,
    [switch] $SkipBuild,
    [switch] $SkipTests,
    [switch] $SkipMigrationCheck
)

. (Join-Path $PSScriptRoot 'common.ps1')

Invoke-GmaScript `
    -Path (Join-Path $PSScriptRoot 'update-solutions.ps1') `
    -Arguments @{ Check = $true }

Invoke-GmaScript `
    -Path (Join-Path $PSScriptRoot 'check-source-packages.ps1') `
    -Arguments @{ SkipRestore = $true; SkipBuild = $true }

if (-not $SkipRestore) {
    Invoke-GmaScript -Path (Join-Path $PSScriptRoot 'restore.ps1')
}

if (-not $SkipBuild) {
    Invoke-GmaDotNet -Arguments @('build', (Join-GmaPath 'BunkFy.slnx'), '--no-restore', '-warnaserror:MSB3277', '-m:1')
}

if (-not $SkipMigrationCheck) {
    Invoke-GmaScript `
        -Path (Join-Path $PSScriptRoot 'check-migrations.ps1') `
        -Arguments @{ NoBuild = $true }
}

if (-not $SkipTests) {
    Invoke-GmaScript `
        -Path (Join-Path $PSScriptRoot 'test-fast.ps1') `
        -Arguments @{ NoBuild = $true }
}
