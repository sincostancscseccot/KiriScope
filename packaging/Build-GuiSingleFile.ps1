[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$Configuration = 'Release',

    [string]$RuntimeIdentifier = 'win-x64',

    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$CodeSigningCertificateThumbprint,

    [string]$TimestampServer = 'http://timestamp.digicert.com',

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $repositoryRoot "artifacts\releases\$Version"
$stagingDirectory = Join-Path $artifactDirectory '.single-file-staging'
$outputFileName = "KiriScope.Gui-$Version-$RuntimeIdentifier.exe"
$outputPath = Join-Path $artifactDirectory $outputFileName

function Invoke-Dotnet {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Publish-SingleFile {
    param(
        [string]$ProjectPath,
        [string]$OutputDirectory,
        [string]$ProjectRuntimeIdentifier,
        [string[]]$AdditionalProperties = @()
    )

    $arguments = @(
        'publish',
        $ProjectPath,
        '--configuration', $Configuration,
        '--runtime', $ProjectRuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $OutputDirectory,
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        "-p:Version=$Version"
    ) + $AdditionalProperties
    Invoke-Dotnet $arguments
}

function Resolve-CodeSigningCertificate {
    if ([string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)) {
        return $null
    }

    $certificatePath = "Cert:\CurrentUser\My\$CodeSigningCertificateThumbprint"
    if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
        throw "The requested code-signing certificate was not found in CurrentUser\My: $CodeSigningCertificateThumbprint"
    }

    $certificate = Get-Item -LiteralPath $certificatePath
    if (-not $certificate.HasPrivateKey) {
        throw "The requested code-signing certificate has no private key: $CodeSigningCertificateThumbprint"
    }

    return $certificate
}

function Remove-StagingDirectory {
    if (-not (Test-Path -LiteralPath $stagingDirectory -PathType Container)) {
        return
    }

    $releaseRoot = [System.IO.Path]::GetFullPath($artifactDirectory).TrimEnd([char[]]@('\', '/'))
    $stagingRoot = [System.IO.Path]::GetFullPath($stagingDirectory)
    $requiredPrefix = $releaseRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $stagingRoot.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a staging path outside the new release directory: $stagingRoot"
    }

    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}

if ($RuntimeIdentifier -ne 'win-x64') {
    throw 'The GUI single-file bundle currently supports win-x64 only. It embeds a separate x86 worker for explicitly enabled runtime capture.'
}

if (Test-Path -LiteralPath $artifactDirectory) {
    throw "Release output already exists and will not be overwritten: $artifactDirectory"
}

if (-not $SkipTests) {
    Invoke-Dotnet @('test', (Join-Path $repositoryRoot 'tests\KiriScope.Core.Tests\KiriScope.Core.Tests.csproj'), '--configuration', $Configuration)
}

New-Item -ItemType Directory -Path $artifactDirectory | Out-Null
New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

try {
    $workerX64Directory = Join-Path $stagingDirectory 'workers\x64'
    $workerX86Directory = Join-Path $stagingDirectory 'workers\x86'
    Publish-SingleFile (Join-Path $repositoryRoot 'src\KiriScope.Worker.X64\KiriScope.Worker.X64.csproj') $workerX64Directory 'win-x64'
    Publish-SingleFile (Join-Path $repositoryRoot 'src\KiriScope.Worker.X86\KiriScope.Worker.X86.csproj') $workerX86Directory 'win-x86'

    $workerX64Path = Join-Path $workerX64Directory 'KiriScope.Worker.X64.exe'
    $workerX86Path = Join-Path $workerX86Directory 'KiriScope.Worker.X86.exe'
    foreach ($workerPath in @($workerX64Path, $workerX86Path)) {
        if (-not (Test-Path -LiteralPath $workerPath -PathType Leaf)) {
            throw "Single-file worker publish did not create the required executable: $workerPath"
        }
    }

    $guiDirectory = Join-Path $stagingDirectory 'gui'
    Publish-SingleFile (Join-Path $repositoryRoot 'src\KiriScope.Gui\KiriScope.Gui.csproj') $guiDirectory $RuntimeIdentifier @(
        "-p:BundledWorkerX64Path=$workerX64Path",
        "-p:BundledWorkerX86Path=$workerX86Path"
    )

    $publishedGuiPath = Join-Path $guiDirectory 'KiriScope.Gui.exe'
    if (-not (Test-Path -LiteralPath $publishedGuiPath -PathType Leaf)) {
        throw "Single-file GUI publish did not create the required executable: $publishedGuiPath"
    }

    Move-Item -LiteralPath $publishedGuiPath -Destination $outputPath

    $certificate = Resolve-CodeSigningCertificate
    if ($null -ne $certificate) {
        $signature = Set-AuthenticodeSignature -LiteralPath $outputPath -Certificate $certificate -TimestampServer $TimestampServer
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "Authenticode signing failed for $outputPath with status $($signature.Status): $($signature.StatusMessage)"
        }
    }
}
finally {
    Remove-StagingDirectory
}

$file = Get-Item -LiteralPath $outputPath -ErrorAction Stop
$hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
$signatureStatus = (Get-AuthenticodeSignature -FilePath $file.FullName).Status.ToString()
[pscustomobject]@{
    Version = $Version
    Gui = $file.FullName
    Length = $file.Length
    Sha256 = $hash.Hash
    AuthenticodeStatus = $signatureStatus
    RuntimeWorkers = 'Embedded; extracted to the user temporary directory only after explicit runtime-capture consent.'
} | Format-List
