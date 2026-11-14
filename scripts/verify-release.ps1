param(
    [Parameter(Mandatory)][string]$ArtifactsDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$AssetsPath,
    [Parameter(Mandatory)][string]$ExpectedSignerThumbprint,
    [ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedCommit
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-signature.ps1')
$signer = ConvertTo-SignerThumbprint $ExpectedSignerThumbprint
$artifacts = Get-Item -LiteralPath $ArtifactsDirectory -Force
if (-not $artifacts.PSIsContainer -or ($artifacts.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'ArtifactsDirectory must be a directory.'
}
$assets = (Get-Item -LiteralPath $AssetsPath).FullName

function Assert-BinaryVersion([string]$path) {
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion
    if ($productVersion -notmatch ('^' + [regex]::Escape($Version) + '(?:\+[0-9a-f]{40})?$')) {
        throw "$path has product version '$productVersion'. Expected $Version."
    }
    if ($ExpectedCommit -and $productVersion -ne "$Version+$ExpectedCommit") {
        throw "$path has product version '$productVersion'. Expected commit $ExpectedCommit."
    }
}

function Assert-BinaryArchitecture([string]$path, [string]$rid) {
    $expectedMachine = if ($rid -eq 'win-x64') { 0x8664 } else { 0xAA64 }
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($path))
    try {
        $reader.BaseStream.Position = 0x3C
        $reader.BaseStream.Position = $reader.ReadUInt32()
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne $expectedMachine) {
            throw "$path is not a $rid executable."
        }
    }
    finally {
        $reader.Dispose()
    }
}

$rids = 'win-x64', 'win-arm64'
$expectedFiles = @(
    foreach ($rid in $rids) {
        "Carousel-$rid.zip"
        "Carousel.Setup-$Version-$rid.exe"
    }
)
$releaseFiles = @(Get-ChildItem -LiteralPath $artifacts.FullName -File -Force |
    Where-Object { $_.Extension -in '.exe', '.zip' })
if ($releaseFiles.Count -ne $expectedFiles.Count -or
    (Compare-Object $expectedFiles $releaseFiles.Name -CaseSensitive)) {
    throw "Expected exactly these release files: $($expectedFiles -join ', '). Found: $($releaseFiles.Name -join ', ')."
}
if ($releaseFiles | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
    throw 'Release files must be regular files.'
}

$verificationRoot = Join-Path $artifacts.FullName ('.verify-release-' + [guid]::NewGuid().ToString('N'))
$verificationDirectory = New-Item -Path $verificationRoot -ItemType Directory
try {
    foreach ($rid in $rids) {
        $setup = Join-Path $artifacts.FullName "Carousel.Setup-$Version-$rid.exe"
        Assert-ReleaseSignature $setup $signer
        Assert-BinaryVersion $setup
        Assert-BinaryArchitecture $setup $rid

        $app = Join-Path $verificationDirectory.FullName $rid
        $zip = Join-Path $artifacts.FullName "Carousel-$rid.zip"
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $app)
        $launcher = Join-Path $app 'Carousel.exe'
        Assert-ReleaseSignature $launcher $signer
        Assert-BinaryVersion $launcher
        Assert-BinaryArchitecture $launcher $rid
        Assert-BinaryArchitecture (Join-Path $app 'Carousel.dll') $rid
        foreach ($name in 'Carousel.dll', 'Flipper.Core.dll') {
            $binary = Join-Path $app $name
            Assert-ReleaseSignature $binary $signer
            Assert-BinaryVersion $binary
        }

        $verifyRuntime = Join-Path $PSScriptRoot 'verify-onnx-runtime.ps1'
        & $verifyRuntime -PublishDirectory $app -RuntimeIdentifier $rid -AssetsPath $assets
    }
    Write-Host "Verified the four signed Carousel $Version release files."
}
finally {
    $resolved = Get-Item -LiteralPath $verificationDirectory.FullName -Force
    $prefix = $artifacts.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        ($resolved.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to remove a verification directory outside the artifacts directory: $($resolved.FullName)"
    }
    Remove-Item -LiteralPath $resolved.FullName -Recurse -Force
}
