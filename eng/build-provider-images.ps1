. (Join-Path $PSScriptRoot 'common.ps1')

$fixtureImage = 'bunkfy-provider-minio:9e49d5e7a648-go1.27.3'
$fixtureSource = '9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a'
$fixtureDirectory = Join-Path $PSScriptRoot 'fixtures/minio'
$fixtureDockerfile = Join-Path $fixtureDirectory 'Minio.SecurityFixed.Dockerfile'
$fixtureRecipeHash = (Get-FileHash -LiteralPath $fixtureDockerfile -Algorithm SHA256).Hash.ToLowerInvariant()
$fixtureExisting = & docker image ls --quiet --no-trunc --filter "reference=$fixtureImage"
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect local provider fixture images.' }
$fixtureLabels = $null
if ($fixtureExisting) {
    $fixtureLabels = (& docker image inspect $fixtureImage --format '{{json .Config.Labels}}') | ConvertFrom-Json -AsHashtable
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the existing provider fixture provenance.' }
}
if (-not $fixtureLabels -or $fixtureLabels['io.bunkfy.fixture.recipe-sha256'] -ne $fixtureRecipeHash -or
    $fixtureLabels['org.opencontainers.image.revision'] -ne $fixtureSource) {
    & docker build --tag $fixtureImage --file $fixtureDockerfile `
        --label "io.bunkfy.fixture.recipe-sha256=$fixtureRecipeHash" $fixtureDirectory
    if ($LASTEXITCODE -ne 0) { throw 'The pinned MinIO provider fixture build failed.' }
}
$fixtureIdentity = (& docker image inspect $fixtureImage) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the built provider fixture.' }
if ($fixtureIdentity[0].Config.Labels.'org.opencontainers.image.revision' -ne $fixtureSource -or
    $fixtureIdentity[0].Config.Labels.'io.bunkfy.fixture.recipe-sha256' -ne $fixtureRecipeHash) {
    throw 'Provider fixture source or recipe identity mismatch.'
}
$fixtureEvidence = Join-GmaPath 'artifacts/provider-images'
[System.IO.Directory]::CreateDirectory($fixtureEvidence) | Out-Null
@{
    image = $fixtureImage
    imageId = $fixtureIdentity[0].Id
    sourceRevision = $fixtureSource
    recipeSha256 = $fixtureRecipeHash
    fixtureOnly = $true
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureEvidence 'minio.json') -Encoding utf8NoBOM
Write-Host "Pinned provider fixture prepared: $fixtureImage"
