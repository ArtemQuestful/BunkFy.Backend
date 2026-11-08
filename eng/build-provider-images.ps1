. (Join-Path $PSScriptRoot 'common.ps1')

$fixtureImage = 'bunkfy-provider-silo:c3ad836aed0d-nonroot-v1'
$fixtureSource = 'c3ad836aed0d8a7d23c2e44a5eaa3009d08525e3'
$fixtureBase = 'pgsty/silo:RELEASE.2026-10-26T00-00-00Z@sha256:7bb2aff6b89efe1cb1303c10fc21d579447434ed06649ac9ec05a5e2dbf30ad7'
$fixtureDirectory = Join-Path $PSScriptRoot 'fixtures/minio'
$fixtureDockerfile = Join-Path $fixtureDirectory 'Silo.TestFixture.Dockerfile'
$fixtureRecipeHash = (Get-FileHash -LiteralPath $fixtureDockerfile -Algorithm SHA256).Hash.ToLowerInvariant()
$fixturePolicy = Get-Content -LiteralPath (Join-GmaPath '.gma/preview-storage-review.json') -Raw | ConvertFrom-Json
if ($fixturePolicy.fixtureRecipeSha256 -cne $fixtureRecipeHash -or $fixturePolicy.fixtureImage -cne $fixtureImage -or
    $fixturePolicy.image -cne $fixtureBase -or $fixturePolicy.sourceCommit -cne $fixtureSource) {
    throw 'The provider fixture recipe is outside the reviewed immutable image policy.'
}
$baseExisting = & docker image ls --quiet --no-trunc --filter "reference=$fixtureBase"
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the provider base image.' }
if (-not $baseExisting) {
    & docker pull --platform linux/amd64 $fixtureBase
    if ($LASTEXITCODE -ne 0) { throw 'The pinned provider base image pull failed.' }
}
$baseConfigDigest = & python3 (Join-Path $PSScriptRoot 'operations/image-config-digest.py') $fixtureBase
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the provider base configuration digest.' }
$baseIdentity = @((& docker image inspect $fixtureBase) | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0 -or $baseIdentity.Count -ne 1 -or
    $baseConfigDigest -cne $fixturePolicy.imageConfigDigest -or
    $baseIdentity[0].Os -cne 'linux' -or $baseIdentity[0].Architecture -cne 'amd64' -or
    ($baseIdentity[0].RootFS.Layers -join ',') -cne ($fixturePolicy.baseLayers -join ',')) {
    throw 'The immutable provider base configuration, platform or filesystem identity changed.'
}
$fixtureExisting = & docker image ls --quiet --no-trunc --filter "reference=$fixtureImage"
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect local provider fixture images.' }
$fixtureLabels = $null
if ($fixtureExisting) {
    $fixtureLabels = (& docker image inspect $fixtureImage --format '{{json .Config.Labels}}') | ConvertFrom-Json -AsHashtable
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the existing provider fixture provenance.' }
}
if (-not $fixtureLabels -or $fixtureLabels['io.bunkfy.fixture.recipe-sha256'] -ne $fixtureRecipeHash -or
    $fixtureLabels['org.opencontainers.image.revision'] -ne $fixtureSource) {
    & docker build --platform linux/amd64 --tag $fixtureImage --file $fixtureDockerfile `
        --label "io.bunkfy.fixture.recipe-sha256=$fixtureRecipeHash" $fixtureDirectory
    if ($LASTEXITCODE -ne 0) { throw 'The pinned non-root provider fixture build failed.' }
}
$fixtureEvidence = Join-GmaPath 'artifacts/provider-images'
[System.IO.Directory]::CreateDirectory($fixtureEvidence) | Out-Null
$descriptorProof = Join-Path $fixtureEvidence 'silo-image-descriptors.json'
$fixtureConfigDigest = & python3 (Join-Path $PSScriptRoot 'operations/image-config-digest.py') $fixtureImage $descriptorProof
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the built provider configuration digest.' }
$fixtureIdentity = @((& docker image inspect $fixtureImage) | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the built provider fixture.' }
if ($fixtureIdentity.Count -ne 1 -or $fixtureIdentity[0].Config.User -cne '10001:10001' -or
    ($fixtureIdentity[0].Config.Entrypoint -join ',') -cne '/usr/bin/docker-entrypoint.sh' -or
    ($fixtureIdentity[0].Config.Cmd -join ',') -cne 'server,/data,--address,:9000' -or
    $fixtureIdentity[0].Os -cne 'linux' -or $fixtureIdentity[0].Architecture -cne 'amd64' -or
    $fixtureIdentity[0].Config.Labels.'org.opencontainers.image.revision' -cne $fixtureSource -or
    $fixtureIdentity[0].Config.Labels.'io.bunkfy.fixture.recipe-sha256' -cne $fixtureRecipeHash -or
    $fixtureIdentity[0].RootFS.Layers.Count -ne ($fixturePolicy.baseLayers.Count + 1) -or
    ($fixtureIdentity[0].RootFS.Layers[0..($fixturePolicy.baseLayers.Count - 1)] -join ',') -cne ($fixturePolicy.baseLayers -join ',')) {
    throw 'Provider fixture user, source, recipe or inherited filesystem identity mismatch.'
}
$fixtureEvidence = Join-GmaPath 'artifacts/provider-images'
[System.IO.Directory]::CreateDirectory($fixtureEvidence) | Out-Null
@{
    schemaVersion = 1
    image = $fixtureImage
    imageId = $fixtureConfigDigest
    imageDescriptors = Get-Content -LiteralPath $descriptorProof -Raw | ConvertFrom-Json -AsHashtable
    sourceRevision = $fixtureSource
    recipeSha256 = $fixtureRecipeHash
    baseImage = $fixtureBase
    baseImageId = $baseConfigDigest
    baseLayers = @($baseIdentity[0].RootFS.Layers)
    imageLayers = @($fixtureIdentity[0].RootFS.Layers)
    user = $fixtureIdentity[0].Config.User
    entrypoint = @($fixtureIdentity[0].Config.Entrypoint)
    command = @($fixtureIdentity[0].Config.Cmd)
    platform = 'linux/amd64'
    fixtureOnly = $true
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureEvidence 'minio.json') -Encoding utf8NoBOM
Write-Host "Pinned provider fixture prepared: $fixtureImage"
