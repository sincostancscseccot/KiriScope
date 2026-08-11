[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$Configuration = 'Release',

    [string]$RuntimeIdentifier = 'win-x64',

    [string]$InnoSetupCompilerPath,

    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$CodeSigningCertificateThumbprint,

    [string]$TimestampServer = 'http://timestamp.digicert.com',

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $repositoryRoot "artifacts\releases\$Version"
$portableName = "KiriScope-$Version-$RuntimeIdentifier"
$portableDirectory = Join-Path $artifactDirectory $portableName
$portableArchive = Join-Path $artifactDirectory "$portableName.zip"
$installerScript = Join-Path $PSScriptRoot 'installer\KiriScope.iss'

function Invoke-Dotnet {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Publish-Project {
    param(
        [string]$ProjectPath,
        [string]$OutputDirectory,
        [string]$ProjectRuntimeIdentifier
    )

    Invoke-Dotnet @(
        'publish',
        $ProjectPath,
        '--configuration', $Configuration,
        '--runtime', $ProjectRuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $OutputDirectory,
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        "-p:Version=$Version"
    )
}

function Resolve-InnoSetupCompiler {
    if (-not [string]::IsNullOrWhiteSpace($InnoSetupCompilerPath)) {
        if (-not (Test-Path -LiteralPath $InnoSetupCompilerPath -PathType Leaf)) {
            throw "The supplied Inno Setup compiler does not exist: $InnoSetupCompilerPath"
        }

        return (Resolve-Path -LiteralPath $InnoSetupCompilerPath).Path
    }

    $candidates = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ($null -eq $compiler) {
        throw 'Inno Setup 6 compiler was not found. Install JRSoftware.InnoSetup or pass -InnoSetupCompilerPath.'
    }

    return $compiler
}

function Test-PortableArchive {
    param(
        [string]$ArchivePath,
        [string]$PortableName
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = $archive.Entries.FullName | ForEach-Object { $_.Replace('\', '/') }
        $requiredEntries = @(
            "$PortableName/KiriScope.Gui.exe",
            "$PortableName/KiriScope.Cli.exe",
            "$PortableName/workers/x64/KiriScope.Worker.X64.exe",
            "$PortableName/workers/x86/KiriScope.Worker.X86.exe",
            "$PortableName/plugins/knowledge-base.json"
        )
        foreach ($requiredEntry in $requiredEntries) {
            if ($requiredEntry -notin $entries) {
                throw "Portable archive is missing the required entry: $requiredEntry"
            }
        }

        $unexpectedBuildArtifacts = $entries | Where-Object { $_ -match '/plugins/templates/.+/(bin|obj)/' }
        if ($unexpectedBuildArtifacts) {
            throw "Portable archive contains excluded plugin-template build artifacts: $($unexpectedBuildArtifacts -join ', ')"
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-ReleaseFileRecord {
    param(
        [string]$Path,
        [string]$ReleaseRoot
    )

    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
    $signatureStatus = 'NotApplicable'
    $signer = $null
    $thumbprint = $null
    $timestamp = $null
    if ([string]::Equals($file.Extension, '.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
        $signature = Get-AuthenticodeSignature -FilePath $file.FullName
        $signatureStatus = $signature.Status.ToString()
        $signer = if ($null -eq $signature.SignerCertificate) { $null } else { $signature.SignerCertificate.Subject }
        $thumbprint = if ($null -eq $signature.SignerCertificate) { $null } else { $signature.SignerCertificate.Thumbprint }
        $timestamp = if ($null -eq $signature.TimeStamperCertificate) { $null } else { $signature.TimeStamperCertificate.Subject }
    }

    if (-not $file.FullName.StartsWith($ReleaseRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release file is outside the release root: $($file.FullName)"
    }

    $relativePath = $file.FullName.Substring($ReleaseRoot.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
    return [ordered]@{
        Path = $relativePath
        Length = $file.Length
        Sha256 = $hash.Hash
        AuthenticodeStatus = $signatureStatus
        SignerSubject = $signer
        SignerThumbprint = $thumbprint
        TimestampAuthority = $timestamp
    }
}

function Resolve-CodeSigningCertificate {
    if ([string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)) {
        return $null
    }

    $certificatePath = "Cert:\CurrentUser\My\$CodeSigningCertificateThumbprint"
    if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
        throw "The requested code-signing certificate was not found in CurrentUser\\My: $CodeSigningCertificateThumbprint"
    }

    $certificate = Get-Item -LiteralPath $certificatePath
    if (-not $certificate.HasPrivateKey) {
        throw "The requested code-signing certificate has no private key: $CodeSigningCertificateThumbprint"
    }

    return $certificate
}

function Sign-ReleaseFiles {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string[]]$Paths
    )

    if ($null -eq $Certificate) {
        return
    }

    foreach ($path in $Paths) {
        $signature = Set-AuthenticodeSignature -LiteralPath $path -Certificate $Certificate -TimestampServer $TimestampServer
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "Authenticode signing failed for $path with status $($signature.Status): $($signature.StatusMessage)"
        }
    }
}

if ($RuntimeIdentifier -ne 'win-x64') {
    throw 'This release layout currently supports win-x64 controllers only. The x86 worker is included separately.'
}

if (Test-Path -LiteralPath $artifactDirectory) {
    throw "Release output already exists and will not be overwritten: $artifactDirectory"
}

if (-not $SkipTests) {
    Invoke-Dotnet @('test', (Join-Path $repositoryRoot 'tests\KiriScope.Core.Tests\KiriScope.Core.Tests.csproj'), '--configuration', $Configuration)
}

New-Item -ItemType Directory -Path $artifactDirectory | Out-Null
New-Item -ItemType Directory -Path $portableDirectory | Out-Null
$codeSigningCertificate = Resolve-CodeSigningCertificate

Publish-Project (Join-Path $repositoryRoot 'src\KiriScope.Cli\KiriScope.Cli.csproj') $portableDirectory $RuntimeIdentifier
Publish-Project (Join-Path $repositoryRoot 'src\KiriScope.Gui\KiriScope.Gui.csproj') $portableDirectory $RuntimeIdentifier
Publish-Project (Join-Path $repositoryRoot 'src\KiriScope.Worker.X64\KiriScope.Worker.X64.csproj') (Join-Path $portableDirectory 'workers\x64') 'win-x64'
Publish-Project (Join-Path $repositoryRoot 'src\KiriScope.Worker.X86\KiriScope.Worker.X86.csproj') (Join-Path $portableDirectory 'workers\x86') 'win-x86'

$requiredFiles = @(
    (Join-Path $portableDirectory 'KiriScope.Gui.exe'),
    (Join-Path $portableDirectory 'KiriScope.Cli.exe'),
    (Join-Path $portableDirectory 'workers\x64\KiriScope.Worker.X64.exe'),
    (Join-Path $portableDirectory 'workers\x86\KiriScope.Worker.X86.exe')
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Release publish did not create the required file: $requiredFile"
    }
}

Sign-ReleaseFiles -Certificate $codeSigningCertificate -Paths $requiredFiles

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination (Join-Path $portableDirectory 'README.md')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $portableDirectory 'THIRD_PARTY_NOTICES.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PORTABLE_README.txt') -Destination (Join-Path $portableDirectory 'START_HERE.txt')

Compress-Archive -LiteralPath $portableDirectory -DestinationPath $portableArchive -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $portableArchive -PathType Leaf)) {
    throw "Portable archive was not created: $portableArchive"
}
Test-PortableArchive -ArchivePath $portableArchive -PortableName $portableName

$innoSetupCompiler = Resolve-InnoSetupCompiler
& $innoSetupCompiler "/DMyAppVersion=$Version" "/DSourceDir=$portableDirectory" "/DOutputDir=$artifactDirectory" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $artifactDirectory "KiriScope-Setup-$Version-$RuntimeIdentifier.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Installer was not created: $installerPath"
}

Sign-ReleaseFiles -Certificate $codeSigningCertificate -Paths @($installerPath)

$releaseManifestPath = Join-Path $artifactDirectory "KiriScope-$Version-release-manifest.json"
$releaseManifest = [ordered]@{
    SchemaVersion = '1.0'
    Product = 'KiriScope'
    Version = $Version
    RuntimeIdentifier = $RuntimeIdentifier
    CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    SigningRequested = -not [string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)
    Files = @(
        Get-ReleaseFileRecord -Path $portableArchive -ReleaseRoot $artifactDirectory
        Get-ReleaseFileRecord -Path $installerPath -ReleaseRoot $artifactDirectory
        Get-ReleaseFileRecord -Path $requiredFiles[0] -ReleaseRoot $artifactDirectory
        Get-ReleaseFileRecord -Path $requiredFiles[1] -ReleaseRoot $artifactDirectory
        Get-ReleaseFileRecord -Path $requiredFiles[2] -ReleaseRoot $artifactDirectory
        Get-ReleaseFileRecord -Path $requiredFiles[3] -ReleaseRoot $artifactDirectory
    )
    Verification = [ordered]@{
        Sha256 = 'Get-FileHash -LiteralPath <path> -Algorithm SHA256'
        Authenticode = 'Get-AuthenticodeSignature -FilePath <path> | Format-List Status,StatusMessage,SignerCertificate,TimeStamperCertificate'
    }
}
$releaseManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $releaseManifestPath -Encoding utf8

[pscustomobject]@{
    Version = $Version
    PortableDirectory = $portableDirectory
    PortableArchive = $portableArchive
    Installer = $installerPath
    ReleaseManifest = $releaseManifestPath
    Gui = Join-Path $portableDirectory 'KiriScope.Gui.exe'
    Cli = Join-Path $portableDirectory 'KiriScope.Cli.exe'
    WorkerX64 = Join-Path $portableDirectory 'workers\x64\KiriScope.Worker.X64.exe'
    WorkerX86 = Join-Path $portableDirectory 'workers\x86\KiriScope.Worker.X86.exe'
} | Format-List
