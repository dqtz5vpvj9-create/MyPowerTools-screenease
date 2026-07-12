<#
.SYNOPSIS
Builds the ScreenEase MyPowerTools adapter and stages its package.

.PARAMETER MyPowerToolsRepoRoot
Absolute or relative path to the MyPowerTools superproject. When omitted, the
script searches ancestor and sibling MyPowerTools directories.
#>

[CmdletBinding()]
param(
    [string] $MyPowerToolsRepoRoot,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'

$ToolRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$AdapterProject = Join-Path $ToolRoot 'current-integration\src\ScreenEase.MyPowerTools\ScreenEase.MyPowerTools.csproj'
$PackageTemplate = Join-Path $ToolRoot 'current-integration\modules\screenease'
$ArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $ToolRoot 'artifacts'))
$PackageOutput = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot 'package'))

function Test-MyPowerToolsRoot {
    param([Parameter(Mandatory = $true)][string] $Candidate)

    return (Test-Path -LiteralPath (Join-Path $Candidate 'MyPowerTools.slnx') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Candidate 'src\MyPowerTools.Abstractions\MyPowerTools.Abstractions.csproj') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Candidate 'src\MyPowerTools.Protocol\MyPowerTools.Protocol.csproj') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Candidate 'src\MyPowerTools.Platform.Abstractions\MyPowerTools.Platform.Abstractions.csproj') -PathType Leaf)
}

function Resolve-MyPowerToolsRoot {
    if (-not [string]::IsNullOrWhiteSpace($MyPowerToolsRepoRoot)) {
        $explicitRoot = [System.IO.Path]::GetFullPath($MyPowerToolsRepoRoot)
        if (-not (Test-MyPowerToolsRoot -Candidate $explicitRoot)) {
            throw "MyPowerToolsRepoRoot does not contain the required MyPowerTools solution and public projects: $explicitRoot"
        }
        return $explicitRoot
    }

    $environmentRoot = [Environment]::GetEnvironmentVariable('MPT_REPO_ROOT')
    if (-not [string]::IsNullOrWhiteSpace($environmentRoot)) {
        $environmentRoot = [System.IO.Path]::GetFullPath($environmentRoot)
        if (Test-MyPowerToolsRoot -Candidate $environmentRoot) {
            return $environmentRoot
        }
    }

    $visited = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $directory = [System.IO.DirectoryInfo]::new($ToolRoot)
    while ($null -ne $directory) {
        foreach ($candidate in @($directory.FullName, (Join-Path $directory.FullName 'MyPowerTools'))) {
            $candidateFull = [System.IO.Path]::GetFullPath($candidate)
            if ($visited.Add($candidateFull) -and (Test-MyPowerToolsRoot -Candidate $candidateFull)) {
                return $candidateFull
            }
        }
        $directory = $directory.Parent
    }

    throw 'Unable to locate MyPowerTools. Pass -MyPowerToolsRepoRoot <path> or set MPT_REPO_ROOT.'
}

function Copy-PackageTemplate {
    if (-not (Test-Path -LiteralPath $PackageTemplate -PathType Container)) {
        throw "ScreenEase package template is missing: $PackageTemplate"
    }

    $artifactsPrefix = $ArtifactsRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $PackageOutput.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Package output escaped the tool artifacts root: $PackageOutput"
    }
    if (Test-Path -LiteralPath $PackageOutput) {
        Remove-Item -LiteralPath $PackageOutput -Recurse -Force
    }
    New-Item -ItemType Directory -Path $PackageOutput -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $PackageTemplate -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $PackageOutput -Recurse -Force
    }
}

if (-not (Test-Path -LiteralPath $AdapterProject -PathType Leaf)) {
    throw "ScreenEase adapter project is missing: $AdapterProject"
}

$resolvedRepoRoot = Resolve-MyPowerToolsRoot
Copy-PackageTemplate

$dotnet = (Get-Command 'dotnet.exe' -CommandType Application -ErrorAction Stop).Source
$argumentList = @(
    'build',
    $AdapterProject,
    '--configuration',
    $Configuration,
    '--nologo',
    "-p:MyPowerToolsRepoRoot=$resolvedRepoRoot",
    "-p:ModulePackageRoot=$PackageOutput"
)
if ($NoRestore) {
    $argumentList += '--no-restore'
}

Push-Location -LiteralPath $resolvedRepoRoot
try {
    & $dotnet @argumentList
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "ScreenEase adapter build failed with exit code $exitCode."
    }
} finally {
    Pop-Location
}

$adapterAssembly = Join-Path $PackageOutput 'ScreenEase.MyPowerTools.dll'
if (-not (Test-Path -LiteralPath $adapterAssembly -PathType Leaf)) {
    throw "ScreenEase package output is missing the adapter assembly: $adapterAssembly"
}

Write-Host "ScreenEase package staged at $PackageOutput"
Write-Host "Adapter SHA256: $((Get-FileHash -LiteralPath $adapterAssembly -Algorithm SHA256).Hash)"
