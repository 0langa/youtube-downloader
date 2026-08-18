[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
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
    $OutputDirectory = Join-Path $scriptRoot '..\artifacts\installer'
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$staging = Join-Path $outputRoot ".staging-$Version"
$appDirectory = Join-Path $staging 'app'
$setupDirectory = Join-Path $staging 'setup'
$payloadPath = Join-Path $staging 'TubeForge.Payload.zip'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

function Assert-ChildPath([string] $Path, [string] $Parent) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing operation outside installer output: $resolved"
    }
}

function Invoke-DotNet([string[]] $Arguments) {
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Remove-DirectoryWithRetry([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force
            return
        }
        catch {
            if ($attempt -eq 20) {
                throw
            }
            Start-Sleep -Milliseconds 250
        }
    }
}

function Open-ReadWithRetry([string] $Path) {
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            return [IO.File]::Open(
                $Path,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        }
        catch [IO.IOException] {
            if ($attempt -eq 20) {
                throw
            }
            Start-Sleep -Milliseconds 250
        }
        catch [UnauthorizedAccessException] {
            if ($attempt -eq 20) {
                throw
            }
            Start-Sleep -Milliseconds 250
        }
    }
}

function Get-Sha256([string] $Path) {
    $stream = Open-ReadWithRetry -Path $Path
    try {
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-InstallerPublishWithRetry([string[]] $Arguments, [string] $PublishDirectory) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        & $dotnet @Arguments
        if ($LASTEXITCODE -eq 0) {
            return
        }
        if ($attempt -eq 3) {
            throw "dotnet failed with exit code $LASTEXITCODE."
        }

        & $dotnet build-server shutdown | Out-Null
        Remove-DirectoryWithRetry -Path $PublishDirectory
        [void](New-Item -ItemType Directory -Path $PublishDirectory)
        Start-Sleep -Milliseconds 500
    }
}

function Get-CodeSigningCertificate([string] $Thumbprint) {
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

function Sign-Application(
    [string] $Path,
    [Security.Cryptography.X509Certificates.X509Certificate2] $Certificate
) {
    $parameters = @{
        LiteralPath = $Path
        Certificate = $Certificate
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

function New-PayloadZip([string] $SourceDirectory, [string] $ArchivePath) {
    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open($ArchivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $root = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
            foreach ($file in (Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName)) {
                $name = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
                $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $input = Open-ReadWithRetry -Path $file.FullName
                $output = $entry.Open()
                try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

[void](New-Item -ItemType Directory -Path $outputRoot -Force)
Assert-ChildPath -Path $staging -Parent $outputRoot
if (Test-Path -LiteralPath $staging) {
    Remove-DirectoryWithRetry -Path $staging
}
[void](New-Item -ItemType Directory -Path $appDirectory -Force)
[void](New-Item -ItemType Directory -Path $setupDirectory -Force)

try {
    $certificate = if ($CertificateThumbprint) {
        Get-CodeSigningCertificate -Thumbprint $CertificateThumbprint
    }
    else {
        $null
    }

    Invoke-DotNet @(
        'publish', (Join-Path $repoRoot 'src\TubeForge.App\TubeForge.App.csproj'),
        '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true',
        '--source', 'https://api.nuget.org/v3/index.json',
        '--output', $appDirectory, "-p:Version=$Version",
        # Matches Publish-Release.ps1: one bundled file instead of 265 loose ones, which is what
        # makes the first launch after an install or update fast.
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false'
    )

    $applicationPath = Join-Path $appDirectory 'TubeForge.exe'
    if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
        throw 'Application publish did not produce TubeForge.exe.'
    }
    if ($certificate) {
        Sign-Application -Path $applicationPath -Certificate $certificate
    }
    & (Join-Path $scriptRoot 'Stage-FFmpeg.ps1') `
        -DestinationDirectory $appDirectory `
        -CacheDirectory $FfmpegCacheDirectory

    $files = foreach ($file in (Get-ChildItem -LiteralPath $appDirectory -File -Recurse | Sort-Object FullName)) {
        [ordered]@{
            path = $file.FullName.Substring($appDirectory.Length + 1).Replace('\', '/')
            length = $file.Length
            sha256 = Get-Sha256 -Path $file.FullName
        }
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        product = 'TubeForge'
        version = $Version
        files = @($files)
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $appDirectory 'install-manifest.json'), $manifest + "`n", $utf8NoBom)
    New-PayloadZip -SourceDirectory $appDirectory -ArchivePath $payloadPath

    Invoke-InstallerPublishWithRetry -PublishDirectory $setupDirectory -Arguments @(
        'publish', (Join-Path $repoRoot 'src\TubeForge.Installer\TubeForge.Installer.csproj'),
        '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true',
        '--source', 'https://api.nuget.org/v3/index.json',
        '--output', $setupDirectory, "-p:Version=$Version", "-p:TubeForgePayload=$payloadPath",
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false'
    )

    $setupName = "TubeForge-$Version-win-x64-setup.exe"
    $setupPath = Join-Path $outputRoot $setupName
    Copy-Item -LiteralPath (Join-Path $setupDirectory 'TubeForge.Setup.exe') -Destination $setupPath -Force
    if ($certificate) {
        Sign-Application -Path $setupPath -Certificate $certificate
    }
    $hash = Get-Sha256 -Path $setupPath
    [IO.File]::WriteAllText((Join-Path $outputRoot 'SHA256SUMS.txt'), "$hash  $setupName`n", $utf8NoBom)
    Write-Output $setupPath
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Assert-ChildPath -Path $staging -Parent $outputRoot
        Remove-DirectoryWithRetry -Path $staging
    }
}
