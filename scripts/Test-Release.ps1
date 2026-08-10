[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [string] $ReleaseDirectory,

    [switch] $SkipLaunch,

    [switch] $RequireAuthenticode
)

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $ReleaseDirectory = Join-Path $scriptRoot '..\artifacts\release'
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory)
$verificationRoot = [IO.Path]::GetFullPath((Join-Path $releaseRoot ".verify-$Version"))

function Assert-StrictChildPath {
    param([string] $Path, [string] $Parent)

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $resolvedPath.StartsWith(
            $resolvedParent + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing filesystem operation outside the release directory: $resolvedPath"
    }
}

function Expand-CheckedArchive {
    param([string] $ArchivePath, [string] $Destination)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $archive.Entries) {
            if ([IO.Path]::IsPathRooted($entry.FullName) -or
                $entry.FullName.Split('/', [StringSplitOptions]::RemoveEmptyEntries) -contains '..') {
                throw "Archive contains an unsafe entry: $($entry.FullName)"
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    [IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $Destination)
}

if (-not (Test-Path -LiteralPath $releaseRoot -PathType Container)) {
    throw "Release directory does not exist: $releaseRoot"
}

$checksumPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
$checksumLines = Get-Content -LiteralPath $checksumPath
if ($checksumLines.Count -lt 3) {
    throw 'Checksum manifest is incomplete.'
}

foreach ($line in $checksumLines) {
    if ($line -notmatch '^(?<Hash>[0-9A-F]{64})  (?<Name>[^\\/:*?"<>|]+)$') {
        throw "Malformed checksum line: $line"
    }
    $artifactPath = [IO.Path]::GetFullPath((Join-Path $releaseRoot $Matches.Name))
    Assert-StrictChildPath -Path $artifactPath -Parent $releaseRoot
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "Release artifact is missing: $($Matches.Name)"
    }
    $actual = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
    if ($actual -cne $Matches.Hash.ToUpperInvariant()) {
        throw "Checksum mismatch: $($Matches.Name)"
    }
}

Assert-StrictChildPath -Path $verificationRoot -Parent $releaseRoot
if (Test-Path -LiteralPath $verificationRoot) {
    Remove-Item -LiteralPath $verificationRoot -Recurse -Force
}
[void](New-Item -ItemType Directory -Path $verificationRoot)

try {
    $manifest = Get-Content -LiteralPath (Join-Path $releaseRoot "TubeForge-$Version-manifest.json") -Raw |
        ConvertFrom-Json
    foreach ($model in @('framework-dependent', 'self-contained')) {
        $archivePath = Join-Path $releaseRoot "TubeForge-$Version-win-x64-$model.zip"
        $destination = Join-Path $verificationRoot $model
        [void](New-Item -ItemType Directory -Path $destination)
        Expand-CheckedArchive -ArchivePath $archivePath -Destination $destination
        if (-not (Test-Path -LiteralPath (Join-Path $destination 'TubeForge.exe') -PathType Leaf)) {
            throw "$model archive does not contain TubeForge.exe."
        }
        $ffmpeg = Join-Path $destination 'ffmpeg\ffmpeg.exe'
        foreach ($required in @(
            $ffmpeg,
            (Join-Path $destination 'ffmpeg\FFmpeg-LICENSE.txt'),
            (Join-Path $destination 'ffmpeg\FFmpeg-Builds-LICENSE.txt'),
            (Join-Path $destination 'ffmpeg\BUILD-PROVENANCE.txt'),
            (Join-Path $destination 'THIRD_PARTY_NOTICES.md')
        )) {
            if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
                throw "$model archive is missing required FFmpeg payload: $required"
            }
        }
        $ffmpegVersion = & $ffmpeg -version
        if ($LASTEXITCODE -ne 0 -or
            $ffmpegVersion[0] -notmatch '^ffmpeg version n8\.1\.2-22-g94138f6973-20260717' -or
            $ffmpegVersion -match '--enable-gpl') {
            throw "$model archive contains an unexpected FFmpeg build."
        }

        $coreRuntime = Test-Path -LiteralPath (Join-Path $destination 'coreclr.dll') -PathType Leaf
        if (($model -eq 'self-contained') -ne $coreRuntime) {
            throw "$model archive has an unexpected runtime dependency layout."
        }

        $manifestArtifact = @($manifest.artifacts | Where-Object { $_.dependencyModel -eq $model })
        if ($manifestArtifact.Count -ne 1) {
            throw "$model archive has no unique release-manifest record."
        }
        $signed = [bool]$manifestArtifact[0].authenticodeSigned
        if ($RequireAuthenticode -and -not $signed) {
            throw "$model archive is not declared Authenticode-signed."
        }
        if ($signed) {
            $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $destination 'TubeForge.exe')
            if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
                throw "$model TubeForge.exe signature is not valid: $($signature.Status)"
            }
        }
    }

    if (-not $SkipLaunch) {
        $applicationPath = Join-Path $verificationRoot 'self-contained\TubeForge.exe'
        $reportPath = Join-Path $verificationRoot 'published-app-smoke.json'
        $process = Start-Process `
            -FilePath $applicationPath `
            -ArgumentList '--performance-report', "`"$reportPath`"" `
            -PassThru `
            -WindowStyle Hidden
        try {
            if (-not $process.WaitForExit(60000)) {
                throw 'Published TubeForge did not complete its desktop startup probe within 60 seconds.'
            }
            if ($process.ExitCode -notin @(0, 1)) {
                throw "Published TubeForge desktop startup probe crashed with exit code $($process.ExitCode)."
            }
            if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
                throw 'Published TubeForge did not produce its desktop startup report.'
            }
            $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
            if ($report.schemaVersion -ne 1 -or $report.metrics.uiFrameSamples -lt 30) {
                throw 'Published TubeForge did not render enough frames for a valid desktop startup probe.'
            }
        }
        finally {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id
                [void]$process.WaitForExit(5000)
            }
            $process.Dispose()
        }
    }
}
finally {
    if (Test-Path -LiteralPath $verificationRoot) {
        Assert-StrictChildPath -Path $verificationRoot -Parent $releaseRoot
        Remove-Item -LiteralPath $verificationRoot -Recurse -Force
    }
}

Write-Output "Release $Version passed checksum, archive, dependency-layout, and launch verification."
