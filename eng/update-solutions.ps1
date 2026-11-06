param(
    [switch] $IncludeRootWorkspace,
    [switch] $Check
)

. (Join-Path $PSScriptRoot 'common.ps1')

$repositoryRoot = Get-GmaRepositoryRoot

function Get-WorkspaceRelativePath {
    param(
        [Parameter(Mandatory = $true)][string] $BasePath,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $separator = [System.IO.Path]::DirectorySeparatorChar
    $normalizedBase = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + $separator
    $normalizedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $normalizedPath.StartsWith($normalizedBase, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$normalizedPath' is outside '$normalizedBase'."
    }

    return $normalizedPath.Substring($normalizedBase.Length)
}

function Add-SolutionEntry {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Folders,

        [Parameter(Mandatory = $true)]
        [string] $Folder,

        [Parameter(Mandatory = $true)]
        [ValidateSet('Project', 'File')]
        [string] $Kind,

        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if (-not $Folders.ContainsKey($Folder)) {
        $Folders[$Folder] = [ordered]@{ Projects = [System.Collections.Generic.List[string]]::new(); Files = [System.Collections.Generic.List[string]]::new() }
    }

    $normalized = $Path.Replace('\', '/')
    if ($Kind -eq 'Project') {
        $items = $Folders[$Folder].Projects
    }
    else {
        $items = $Folders[$Folder].Files
    }
    if (-not $items.Contains($normalized)) {
        [void]$items.Add($normalized)
    }
}

function Get-OrdinalSortedStrings {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]] $Values)

    $sorted = [System.Collections.Generic.List[string]]::new()
    foreach ($value in $Values) {
        [void]$sorted.Add($value)
    }

    $sorted.Sort([System.StringComparer]::Ordinal)
    return $sorted
}

function Write-SolutionFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [hashtable] $Folders,

        [switch] $Check
    )

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.AppendLine('<Solution>')
    foreach ($folderName in (Get-OrdinalSortedStrings -Values @($Folders.Keys))) {
        $folder = $Folders[$folderName]
        if ($folder.Projects.Count -eq 0 -and $folder.Files.Count -eq 0) {
            continue
        }

        [void]$builder.AppendLine("  <Folder Name=`"$folderName`">")
        foreach ($file in (Get-OrdinalSortedStrings -Values @($folder.Files))) {
            [void]$builder.AppendLine("    <File Path=`"$file`" />")
        }
        foreach ($project in (Get-OrdinalSortedStrings -Values @($folder.Projects))) {
            [void]$builder.AppendLine("    <Project Path=`"$project`" />")
        }
        [void]$builder.AppendLine('  </Folder>')
    }
    [void]$builder.AppendLine('</Solution>')

    $expectedContent =
        $builder.ToString().Replace("`r`n", "`n").TrimEnd() + "`n"
    if ($Check) {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
            throw "Solution '$Path' does not exist. Run the solution synchronization tool without -Check."
        }

        $normalize = {
            param([string] $Value)
            return $Value.Replace("`r`n", "`n").TrimEnd() + "`n"
        }
        $actualContent = [System.IO.File]::ReadAllText($Path)
        if ((& $normalize $actualContent) -ne (& $normalize $expectedContent)) {
            throw "Solution '$Path' is out of date. Run the solution synchronization tool without -Check."
        }

        return
    }

    [System.IO.File]::WriteAllText($Path, $expectedContent, [System.Text.UTF8Encoding]::new($false))
}

function Get-BackendProjectFolder {
    param([Parameter(Mandatory = $true)][string] $RelativePath)

    $segments = $RelativePath.Replace('\', '/').Split('/')
    if ($segments[0] -eq 'src' -and $segments[1] -eq 'Modules') {
        $suffix = if ($segments[3] -eq 'tests') { '/tests' } else { '' }
        return "/src/Modules/$($segments[2])$suffix/"
    }
    if ($segments[0] -eq 'src' -and $segments[1] -eq 'Adapters') {
        if ($segments[2] -eq 'tests') {
            return '/src/Adapters/tests/'
        }

        return '/src/Adapters/'
    }
    if ($segments[0] -eq 'src' -and $segments[1] -eq 'Shared') {
        return '/src/Shared/'
    }
    if ($segments[0] -eq 'src' -and $segments[1] -eq 'Extensions') {
        if ($segments[2] -eq 'tests') {
            return '/src/Extensions/tests/'
        }

        return '/src/Extensions/'
    }
    if ($segments[0] -eq 'src' -and $segments[1].StartsWith('BunkFy.Host.', [System.StringComparison]::Ordinal)) {
        return '/src/Hosts/'
    }
    if ($segments[0] -eq 'src') {
        return '/src/'
    }
    if ($segments[0] -eq 'tests') {
        return '/tests/'
    }
    if ($segments[0] -eq 'gma') {
        $projectDirectory = Split-Path $RelativePath -Parent
        $ownerDirectory = Split-Path $projectDirectory -Parent
        return "/$($ownerDirectory.Replace('\', '/'))/"
    }

    return '/Other/'
}

function Add-BackendGraph {
    param(
        [Parameter(Mandatory = $true)][hashtable] $Folders,
        [Parameter(Mandatory = $true)][string] $BasePath,
        [string] $PathPrefix = '',
        [string] $FolderPrefix = ''
    )

    $projectRoots = @('src', 'tests', 'gma')
    foreach ($root in $projectRoots) {
        $absoluteRoot = Join-Path $BasePath $root
        if (-not (Test-Path -LiteralPath $absoluteRoot -PathType Container)) {
            continue
        }

        foreach ($project in Get-ChildItem -LiteralPath $absoluteRoot -Recurse -Filter *.csproj -File) {
            $relative = Get-WorkspaceRelativePath -BasePath $BasePath -Path $project.FullName
            if ($relative -match '(^|[\\/])(\.tmp|bin|obj)([\\/]|$)') {
                continue
            }

            $folder = Get-BackendProjectFolder -RelativePath $relative
            Add-SolutionEntry $Folders "$FolderPrefix$folder" Project "$PathPrefix$relative"
        }
    }

    foreach ($relativeRoot in @('docs', 'eng', '.github', 'requests')) {
        $absoluteRoot = Join-Path $BasePath $relativeRoot
        if (-not (Test-Path -LiteralPath $absoluteRoot -PathType Container)) {
            continue
        }

        foreach ($file in Get-ChildItem -LiteralPath $absoluteRoot -Recurse -File |
            Where-Object { $_.Extension -in @('.md', '.mjs', '.ps1', '.yml', '.yaml', '.http') }) {
            $relative = Get-WorkspaceRelativePath -BasePath $BasePath -Path $file.FullName
            $directory = Split-Path $relative -Parent
            Add-SolutionEntry $Folders "$FolderPrefix/$($directory.Replace('\', '/'))/" File "$PathPrefix$relative"
        }
    }

    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $BasePath 'src') -Recurse -File) {
        $relative = Get-WorkspaceRelativePath -BasePath $BasePath -Path $file.FullName
        if ($relative -match '(^|[\\/])(\.tmp|bin|obj)([\\/]|$)') {
            continue
        }
        $normalizedRelative = $relative.Replace('\', '/')
        $isDataGovernanceCatalog =
            $normalizedRelative -like 'src/Modules/*/docs/*.json' -or
            $normalizedRelative -like 'src/Extensions/*/docs/*.json'
        if ($file.Extension -ne '.md' -and -not $isDataGovernanceCatalog) {
            continue
        }

        $directory = Split-Path $relative -Parent
        Add-SolutionEntry $Folders "$FolderPrefix/$($directory.Replace('\', '/'))/" File "$PathPrefix$relative"
    }
}

$solutionImplementation = Join-GmaPath 'gma/framework/eng/sync-solution.ps1'
if (-not (Test-Path -LiteralPath $solutionImplementation -PathType Leaf)) {
    throw 'GMA framework tooling is not mounted. Run eng/gma-update.ps1 -Init first.'
}

$solutionArguments = @{
    RepositoryRoot = $repositoryRoot
    Solution = 'BunkFy.slnx'
    SourceFileExtensions = @('.md')
    SourceFilePathPatterns = @(
        'src/Modules/*/docs/*.json',
        'src/Extensions/*/docs/*.json'
    )
}
if ($Check) {
    $solutionArguments.Check = $true
}

& $solutionImplementation @solutionArguments

if ($IncludeRootWorkspace) {
    $workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot '..\..'))
    $workspaceSolution = Join-Path $workspaceRoot 'BunkFy.Workspace.slnx'
    if (-not (Test-Path -LiteralPath $workspaceSolution -PathType Leaf)) {
        throw "The BunkFy root workspace was not found at '$workspaceSolution'."
    }

    $workspaceFolders = @{}
    foreach ($project in Get-ChildItem -LiteralPath (Join-Path $workspaceRoot 'src') -Recurse -Filter *.csproj -File) {
        $relative = Get-WorkspaceRelativePath -BasePath $workspaceRoot -Path $project.FullName
        Add-SolutionEntry $workspaceFolders '/src/' Project $relative
    }
    Add-BackendGraph -Folders $workspaceFolders -BasePath $repositoryRoot `
        -PathPrefix 'apps/backend/' -FolderPrefix '/apps/backend'
    Add-SolutionEntry $workspaceFolders '/apps/backend/' File 'apps/backend/BunkFy.slnx'

    foreach ($relativeRoot in @('docs', 'eng', '.github')) {
        $absoluteRoot = Join-Path $workspaceRoot $relativeRoot
        if (-not (Test-Path -LiteralPath $absoluteRoot -PathType Container)) {
            continue
        }
        foreach ($file in Get-ChildItem -LiteralPath $absoluteRoot -Recurse -File |
            Where-Object { $_.Extension -in @('.md', '.mjs', '.ps1', '.yml', '.yaml') }) {
            $relative = Get-WorkspaceRelativePath -BasePath $workspaceRoot -Path $file.FullName
            $directory = Split-Path $relative -Parent
            Add-SolutionEntry $workspaceFolders "/$($directory.Replace('\', '/'))/" File $relative
        }
    }

    foreach ($item in @(
        '.gitignore', '.gitmodules', '.gma/release-evidence.json',
        '.gma/repository-security.json', '.gma/security-exceptions.json',
        'Directory.Build.props', 'Directory.Packages.props', 'global.json',
        'LICENSE', 'nuget.config', 'README.md', 'SECURITY.md', 'SUPPORT.md'
    )) {
        if (Test-Path -LiteralPath (Join-Path $workspaceRoot $item) -PathType Leaf) {
            Add-SolutionEntry $workspaceFolders '/Solution Items/' File $item
        }
    }

    $webRoot = Join-Path $workspaceRoot 'apps\web'
    if (Test-Path -LiteralPath $webRoot -PathType Container) {
        foreach ($name in @('package.json', 'pnpm-lock.yaml', 'README.md', 'tsconfig.json', 'vite.config.ts')) {
            if (Test-Path -LiteralPath (Join-Path $webRoot $name) -PathType Leaf) {
                Add-SolutionEntry $workspaceFolders '/apps/web/' File "apps/web/$name"
            }
        }
    }

    Write-SolutionFile -Path $workspaceSolution -Folders $workspaceFolders -Check:$Check
}

if ($Check -and $IncludeRootWorkspace) {
    Write-Host 'Backend and product workspace solutions match the current workspace graph.'
}
elseif ($Check) {
    Write-Host 'Backend solution matches the current workspace graph.'
}
elseif ($IncludeRootWorkspace) {
    Write-Host 'Backend and product workspace solutions updated from the current workspace graph.'
}
else {
    Write-Host 'Backend solution updated from the current workspace graph.'
}
