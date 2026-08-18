[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [string] $OutputDirectory,

    [string] $FfmpegCacheDirectory,

    [ValidatePattern('^[0-9A-Fa-f]{40,64}$')]
    [string] $CertificateThumbprint,

    [uri] $TimestampServer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $scriptRoot '..\artifacts\release'
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $outputRoot ".staging-$Version"))
$project = Join-Path $repoRoot 'src\TubeForge.App\TubeForge.App.csproj'
$fixedZipTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Assert-StrictChildPath {
    param([string] $Path, [string] $Parent)

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $resolvedPath.StartsWith(
            $resolvedParent + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing filesystem operation outside the release output directory: $resolvedPath"
    }
}

function Reset-Directory {
    param([string] $Path, [string] $Parent)

    Assert-StrictChildPath -Path $Path -Parent $Parent
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    [void](New-Item -ItemType Directory -Path $Path)
}

function Invoke-DotNet {
    param([string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Add-ReleaseDocuments {
    param([string] $PublishDirectory)

    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $PublishDirectory
    Copy-Item -LiteralPath (Join-Path $repoRoot 'SECURITY.md') -Destination $PublishDirectory
    $docsDirectory = Join-Path $PublishDirectory 'docs'
    [void](New-Item -ItemType Directory -Path $docsDirectory -Force)
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\INSTALLATION.md') -Destination $docsDirectory
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\EXTRACTION_COMPATIBILITY.md') -Destination $docsDirectory
    $licensePath = Join-Path $repoRoot 'LICENSE'
    if (Test-Path -LiteralPath $licensePath) {
        Copy-Item -LiteralPath $licensePath -Destination $PublishDirectory
    }
}

function Get-CodeSigningCertificate {
    param([string] $Thumbprint)

    foreach ($store in @('CurrentUser', 'LocalMachine')) {
        $path = "Cert:\$store\My\$Thumbprint"
        if (Test-Path -LiteralPath $path) {
            $certificate = Get-Item -LiteralPath $path
            $codeSigningOid = '1.3.6.1.5.5.7.3.3'
            $canSignCode = $certificate.HasPrivateKey -and
                $certificate.NotBefore -le [DateTime]::UtcNow -and
                $certificate.NotAfter -gt [DateTime]::UtcNow -and
                $certificate.EnhancedKeyUsageList.ObjectId.Value -contains $codeSigningOid
            if (-not $canSignCode) {
                throw 'The selected certificate is not a currently valid code-signing certificate with a private key.'
            }
            return $certificate
        }
    }

    throw 'The requested code-signing certificate was not found in CurrentUser or LocalMachine Personal stores.'
}

function Sign-Application {
    param(
        [string] $Path,
        [Security.Cryptography.X509Certificates.X509Certificate2] $Certificate
    )

    $parameters = @{
        LiteralPath  = $Path
        Certificate  = $Certificate
        HashAlgorithm = 'SHA256'
        IncludeChain = 'NotRoot'
    }
    if ($TimestampServer) {
        $parameters.TimestampServer = $TimestampServer.AbsoluteUri
    }

    $signature = Set-AuthenticodeSignature @parameters
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode signing failed for $Path with status $($signature.Status)."
    }
}

function New-DeterministicZip {
    param([string] $SourceDirectory, [string] $ArchivePath)

    Add-Type -AssemblyName System.IO.Compression
    if (Test-Path -LiteralPath $ArchivePath) {
        Remove-Item -LiteralPath $ArchivePath -Force
    }

    $archiveStream = [IO.File]::Open($ArchivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $archiveStream,
            [IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            $sourceRoot = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
            $files = Get-ChildItem -LiteralPath $sourceRoot -File -Recurse |
                Sort-Object { $_.FullName.Substring($sourceRoot.Length + 1) }
            foreach ($file in $files) {
                $entryName = $file.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
                $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedZipTime
                $input = $file.OpenRead()
                $output = $entry.Open()
                try {
                    $input.CopyTo($output)
                }
                finally {
                    $output.Dispose()
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $archiveStream.Dispose()
    }
}

[void](New-Item -ItemType Directory -Path $outputRoot -Force)
Reset-Directory -Path $stagingRoot -Parent $outputRoot

try {
    Invoke-DotNet @(
        'restore', $project,
        '--runtime', 'win-x64',
        '--source', 'https://api.nuget.org/v3/index.json',
        '--force-evaluate'
    )

    $targets = @(
        [ordered]@{ Name = 'framework-dependent'; SelfContained = 'false' },
        [ordered]@{ Name = 'self-contained'; SelfContained = 'true' }
    )
    $certificate = if ($CertificateThumbprint) {
        Get-CodeSigningCertificate -Thumbprint $CertificateThumbprint
    }
    else {
        $null
    }

    $artifacts = @()
    foreach ($target in $targets) {
        $publishDirectory = Join-Path $stagingRoot $target.Name
        [void](New-Item -ItemType Directory -Path $publishDirectory)
        Invoke-DotNet @(
            'publish', $project,
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', $target.SelfContained,
            '--no-restore',
            '--output', $publishDirectory,
            "-p:Version=$Version",
            '-p:ContinuousIntegrationBuild=true',
            "-p:PathMap=$repoRoot=/_/",
            # Single-file publish: the loose layout wrote 265 files per install, and antivirus
            # scanning them made the first launch after every update take 12-16 seconds against
            # about 2 seconds for one bundled file.
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:PublishTrimmed=false',
            '-p:PublishReadyToRun=false'
        )
        Add-ReleaseDocuments -PublishDirectory $publishDirectory
        & (Join-Path $scriptRoot 'Stage-FFmpeg.ps1') `
            -DestinationDirectory $publishDirectory `
            -CacheDirectory $FfmpegCacheDirectory

        $applicationPath = Join-Path $publishDirectory 'TubeForge.exe'
        if (-not (Test-Path -LiteralPath $applicationPath)) {
            throw "Publish did not produce $applicationPath."
        }
        if ($certificate) {
            Sign-Application -Path $applicationPath -Certificate $certificate
        }

        $archiveName = "TubeForge-$Version-win-x64-$($target.Name).zip"
        $archivePath = Join-Path $outputRoot $archiveName
        New-DeterministicZip -SourceDirectory $publishDirectory -ArchivePath $archivePath
        $artifacts += [ordered]@{
            file = $archiveName
            runtime = 'win-x64'
            dependencyModel = $target.Name
            authenticodeSigned = $null -ne $certificate
            sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
            bytes = (Get-Item -LiteralPath $archivePath).Length
        }
    }

    $manifestName = "TubeForge-$Version-manifest.json"
    $manifestPath = Join-Path $outputRoot $manifestName
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $Version
        application = 'TubeForge'
        artifacts = $artifacts
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($manifestPath, $manifest + "`n", $utf8NoBom)

    $checksumInputs = @($artifacts.file) + $manifestName
    $checksumLines = foreach ($name in $checksumInputs) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $outputRoot $name) -Algorithm SHA256).Hash
        "$hash  $name"
    }
    [IO.File]::WriteAllLines((Join-Path $outputRoot 'SHA256SUMS.txt'), $checksumLines, $utf8NoBom)
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Assert-StrictChildPath -Path $stagingRoot -Parent $outputRoot
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

Write-Output "Release artifacts written to $outputRoot"
