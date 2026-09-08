[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Build', 'Release')]
    [string]$Mode,

    [string]$ProjectPath,
    [string]$ArtifactsRoot,
    [string[]]$RuntimeIdentifiers,
    [string]$SmokeRuntimeIdentifier,
    [string]$Tag,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-RepoRoot {
    (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Get-ProjectVersion {
    param([Parameter(Mandatory)][string]$Path)

    [xml]$project = Get-Content -LiteralPath $Path -Raw
    $versions = @(
        $project.Project.PropertyGroup |
            ForEach-Object { $_.Version } |
            Where-Object { $_ }
    )

    if (-not $versions) {
        throw "Unable to determine the project version from '$Path'."
    }

    [string]$versions[0]
}

function Get-RuntimeExecutableName {
    param([Parameter(Mandatory)][string]$RuntimeIdentifier)

    if ($RuntimeIdentifier.StartsWith('win-')) {
        'qr-bulk.exe'
    }
    else {
        'qr-bulk'
    }
}

function Get-ArchivePath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$RuntimeIdentifier,
        [Parameter(Mandatory)][string]$Version
    )

    $name = "qr-bulk-$Version-$RuntimeIdentifier"
    if ($RuntimeIdentifier.StartsWith('win-')) {
        Join-Path $Root "$name.zip"
    }
    else {
        Join-Path $Root "$name.tar.gz"
    }
}

function Copy-DistributionNotices {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$PublishRoot
    )

    foreach ($fileName in @('LICENSE.md', 'NOTICE', 'THIRD-PARTY-NOTICES.md')) {
        $source = Join-Path $RepoRoot $fileName
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Required distribution notice is missing: $source"
        }
        Copy-Item -LiteralPath $source -Destination $PublishRoot -Force
    }

    $licensesRoot = Join-Path $RepoRoot 'licenses'
    if (-not (Test-Path -LiteralPath $licensesRoot -PathType Container)) {
        throw "Required dependency licenses are missing: $licensesRoot"
    }
    Copy-Item -LiteralPath $licensesRoot -Destination $PublishRoot -Recurse -Force
}

function Copy-RuntimeNotices {
    param(
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$RuntimeIdentifier,
        [Parameter(Mandatory)][string]$PublishRoot
    )

    $assetsPath = Join-Path (Split-Path $ProjectPath) 'obj' 'project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
    $runtimePack = @($assets['project']['frameworks']['net10.0']['downloadDependencies'] |
        Where-Object { $_.name -eq "Microsoft.NETCore.App.Runtime.$RuntimeIdentifier" })
    if ($runtimePack.Count -ne 1) {
        throw "Cannot determine the restored runtime pack for $RuntimeIdentifier."
    }
    $packVersion = $runtimePack[0].version.Trim([char[]]'[]').Split(',')[0].Trim()
    $packRoot = Join-Path $assets['project']['restore']['packagesPath'] $runtimePack[0].name.ToLowerInvariant() $packVersion
    $licenseRoot = Join-Path $PublishRoot 'licenses'
    foreach ($name in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
        $source = Join-Path $packRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Required runtime notice is missing: $source"
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $licenseRoot "dotnet-runtime-$name") -Force
    }
}

function Invoke-CommandOrFail {
    param(
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [Parameter(Mandatory)][string]$Description
    )

    & $ScriptBlock
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Get-NativeRuntimeIdentifier {
    $platform = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' }
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if (-not $platform -or $architecture -notin @('x64', 'arm64')) {
        throw "Unsupported build host architecture: $architecture"
    }
    "$platform-$architecture"
}

function Build-PublishArchive {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$ArtifactsRoot,
        [Parameter(Mandatory)][string]$RuntimeIdentifier,
        [Parameter(Mandatory)][string]$Version
    )

    $workRoot = Join-Path $ArtifactsRoot 'work'
    $outRoot = Join-Path $ArtifactsRoot 'out'
    $publishRoot = Join-Path (Join-Path $workRoot $RuntimeIdentifier) 'publish'
    $archivePath = Get-ArchivePath -Root $outRoot -RuntimeIdentifier $RuntimeIdentifier -Version $Version

    if ((Test-Path -LiteralPath $publishRoot) -and @(Get-ChildItem -LiteralPath $publishRoot -Force).Count -gt 0) {
        throw "Publish directory is not empty. Use a fresh artifacts directory: $publishRoot"
    }
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $outRoot -Force | Out-Null

    Invoke-CommandOrFail -Description "Locked restore for $RuntimeIdentifier" -ScriptBlock {
        & dotnet restore $ProjectPath --locked-mode "-p:RuntimeIdentifier=$RuntimeIdentifier" -p:PackAsTool=false -p:SelfContained=true
    }

    $publishArgs = @(
        'publish',
        $ProjectPath,
        '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $publishRoot,
        '-p:PackAsTool=false',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false',
        '-p:ContinuousIntegrationBuild=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )

    $publishArgs += '--no-restore'

    Invoke-CommandOrFail -Description "dotnet publish for $RuntimeIdentifier" -ScriptBlock { & dotnet @publishArgs }

    Copy-DistributionNotices -RepoRoot $RepoRoot -PublishRoot $publishRoot
    Copy-RuntimeNotices -ProjectPath $ProjectPath -RuntimeIdentifier $RuntimeIdentifier -PublishRoot $publishRoot

    if ($RuntimeIdentifier.StartsWith('win-')) {
        $archiveContents = Join-Path $publishRoot '*'
        Compress-Archive -Path $archiveContents -DestinationPath $archivePath -Force
    }
    else {
        Invoke-CommandOrFail -Description "tar archive for $RuntimeIdentifier" -ScriptBlock {
            & tar -czf $archivePath -C $publishRoot .
        }
    }

    $nativeRuntime = if ($SmokeRuntimeIdentifier) { $SmokeRuntimeIdentifier } else { Get-NativeRuntimeIdentifier }
    if ($RuntimeIdentifier -eq $nativeRuntime) {
        $exeName = Get-RuntimeExecutableName -RuntimeIdentifier $RuntimeIdentifier
        $exePath = Join-Path $publishRoot $exeName
        if (-not (Test-Path -LiteralPath $exePath)) {
            throw "Native executable '$exePath' was not produced."
        }

        $smokeRoot = Join-Path $ArtifactsRoot 'smoke'
        $pngPath = Join-Path $smokeRoot 'smoke.png'
        $pdfPath = Join-Path $smokeRoot 'smoke.pdf'
        New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null

        try {
            & $exePath --help | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "--help smoke test failed for '$RuntimeIdentifier'."
            }

            & $exePath --text 'release smoke' --format png --output $pngPath | Out-Null
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $pngPath)) {
                throw "PNG smoke test failed for '$RuntimeIdentifier'."
            }

            & $exePath --text 'release smoke' --label 'Portable labels' --format pdf --output $pdfPath | Out-Null
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $pdfPath)) {
                throw "PDF smoke test failed for '$RuntimeIdentifier'."
            }
        }
        finally {
            foreach ($path in @($pngPath, $pdfPath)) {
                if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
            }
            Remove-Item -LiteralPath $smokeRoot
        }
    }
}

function New-ReleaseArtifacts {
    param(
        [Parameter(Mandatory)][string]$ArtifactsRoot,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$Version
    )

    $assetFiles = @(Get-ChildItem -LiteralPath $ArtifactsRoot -File |
        Where-Object {
            $_.Name -ne 'SHA256SUMS' -and (
                $_.Extension -eq '.nupkg' -or
                $_.Extension -eq '.zip' -or
                $_.Name.EndsWith('.tar.gz')
            )
        } |
        Sort-Object FullName)

    $expected = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64') |
        ForEach-Object { Split-Path (Get-ArchivePath -Root $ArtifactsRoot -RuntimeIdentifier $_ -Version $Version) -Leaf }
    $expected += "QRBulkGen.Cli.$Version.nupkg"
    $difference = @(Compare-Object -ReferenceObject @($expected | Sort-Object) -DifferenceObject @($assetFiles.Name | Sort-Object))
    if ($difference.Count -ne 0) {
        throw 'The release must contain exactly six platform archives and the matching tool package.'
    }

    $checksumsPath = Join-Path $ArtifactsRoot 'SHA256SUMS'
    $checksumLines = foreach ($file in $assetFiles) {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        "$hash  $($file.Name)"
    }
    $checksumLines | Set-Content -LiteralPath $checksumsPath -Encoding utf8

    $notesPath = Join-Path $ArtifactsRoot 'release-notes.md'
    $assetNames = @($assetFiles | ForEach-Object Name) + 'SHA256SUMS'
    $notesLines = @(
        "# QR Bulk CLI $Version",
        '',
        'Generate PNG/SVG QR codes and printable PDF labels offline from text, files, CSV, or pipelines.',
        '',
        'Download the archive matching your OS and CPU and extract it; no .NET installation is required.',
        'Windows: run `.\qr-bulk.exe --help`. Linux/macOS: run `./qr-bulk --help`.',
        'The `.nupkg` is an alternative for .NET 10 SDK users; see the README for installation.',
        '',
        'Assets:',
        ''
    ) + ($assetNames | ForEach-Object { "- $_" })
    $notesLines | Set-Content -LiteralPath $notesPath -Encoding utf8

    $releaseAssets = @($assetFiles | ForEach-Object FullName) + $checksumsPath
    $repository = $env:GITHUB_REPOSITORY
    if ([string]::IsNullOrWhiteSpace($repository)) {
        throw 'GITHUB_REPOSITORY must be set for release creation.'
    }

    $ghArgs = @(
        'release', 'create', $Tag,
        '--repo', $repository,
        '--title', $Tag,
        '--notes-file', $notesPath,
        '--draft',
        '--verify-tag'
    ) + $releaseAssets

    if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN) -and [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        throw 'GH_TOKEN or GITHUB_TOKEN must be set for release creation.'
    }

    Invoke-CommandOrFail -Description "gh release create for $Tag" -ScriptBlock { & gh @ghArgs }
    Invoke-CommandOrFail -Description "gh release publish for $Tag" -ScriptBlock {
        & gh release edit $Tag --repo $repository --draft=false
    }
}

$repoRoot = Get-RepoRoot

if (-not $ProjectPath) {
    $ProjectPath = Join-Path (Join-Path (Join-Path $repoRoot 'src') 'QRBulkGen.Cli') 'QRBulkGen.Cli.csproj'
}

if (-not $ArtifactsRoot) {
    $ArtifactsRoot = Join-Path $repoRoot 'artifacts'
}

$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$repoRoot = (Resolve-Path -LiteralPath $repoRoot).Path

if ($Mode -eq 'Build') {
    if (-not $Version) {
        $Version = Get-ProjectVersion -Path $ProjectPath
    }

    if (-not $RuntimeIdentifiers -or $RuntimeIdentifiers.Count -eq 0) {
        throw 'At least one runtime identifier is required for build mode.'
    }

    foreach ($runtimeIdentifier in $RuntimeIdentifiers) {
        if ($runtimeIdentifier -notin @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')) {
            throw "Unsupported release runtime identifier: $runtimeIdentifier"
        }
        Build-PublishArchive -RepoRoot $repoRoot -ProjectPath $ProjectPath -ArtifactsRoot $ArtifactsRoot -RuntimeIdentifier $runtimeIdentifier -Version $Version
    }
}
elseif ($Mode -eq 'Release') {
    if (-not $Tag) {
        throw 'A tag is required for release mode.'
    }

    if (-not $Version) {
        $Version = Get-ProjectVersion -Path $ProjectPath
    }

    if ($Tag -notmatch '^v\d+\.\d+\.\d+(?:[-+][0-9A-Za-z-.]+)?$') {
        throw "Release tags must be semver tags prefixed with v; got '$Tag'."
    }

    if ($Tag.Substring(1) -ne $Version) {
        throw "Tag '$Tag' does not match project version '$Version'."
    }

    New-ReleaseArtifacts -ArtifactsRoot $ArtifactsRoot -Tag $Tag -Version $Version
}
