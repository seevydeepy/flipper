param(
    [string]$Version,
    [string]$SigningCertificateThumbprint,
    [string]$SignToolPath,
    [string]$TimestampServer = 'http://time.certum.pl'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-signature.ps1')
. (Join-Path $PSScriptRoot 'vc-runtime.ps1')
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function ConvertFrom-GitTag([string]$tag) {
    $text = $tag.Trim()
    if ($text.StartsWith('v') -or $text.StartsWith('V')) {
        $text = $text.Substring(1)
    }

    $cut = $text.IndexOfAny([char[]]@('+', '-'))
    if ($cut -ge 0) {
        $text = $text.Substring(0, $cut)
    }

    if ($text -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
        return $null
    }

    return [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
}

function Get-ParsedTags([string[]]$tags) {
    $parsed = @()
    foreach ($tag in $tags) {
        if ([string]::IsNullOrWhiteSpace($tag)) {
            continue
        }

        $version = ConvertFrom-GitTag $tag
        if ($null -ne $version) {
            $parsed += $version
        }
    }

    return $parsed
}

function Get-NextVersion {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & git fetch origin --tags --quiet
    $ErrorActionPreference = $prev

    $headTags = [string[]](& git tag --points-at HEAD --list 'v*')
    $headParsed = Get-ParsedTags $headTags
    if ($headParsed.Count -gt 0) {
        $existing = $headParsed | Sort-Object | Select-Object -Last 1
        return '{0}.{1}.{2}' -f $existing.Major, $existing.Minor, $existing.Build
    }

    $allTags = [string[]](& git tag --list 'v*')
    $parsed = Get-ParsedTags $allTags
    if ($parsed.Count -eq 0) {
        return '1.0.0'
    }

    $highest = $parsed | Sort-Object | Select-Object -Last 1
    return '{0}.{1}.{2}' -f $highest.Major, $highest.Minor, ($highest.Build + 1)
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-NextVersion
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Version must be a numeric major.minor.patch value.'
}

$sourceRevision = & git rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $sourceRevision -notmatch '^[0-9a-f]{40}$') {
    throw 'Could not determine the current Git commit.'
}

$signer = $null
if (-not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    $signer = ConvertTo-SignerThumbprint $SigningCertificateThumbprint
    if ([string]::IsNullOrWhiteSpace($SignToolPath)) {
        $SignToolPath = (Get-Command signtool.exe -CommandType Application -ErrorAction Stop).Source
    }
    else {
        $SignToolPath = (Get-Item -LiteralPath $SignToolPath).FullName
        if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
            throw 'SignToolPath must point to the SignTool executable.'
        }
    }
    if ([string]::IsNullOrWhiteSpace($TimestampServer)) {
        throw 'TimestampServer is required when signing.'
    }
}
else {
    Write-Host 'Building unsigned draft assets.'
}

$appProject = 'src/Flipper.App/Flipper.App.csproj'
$setupProject = 'src/Flipper.Setup/Flipper.Setup.csproj'
$icon = Join-Path $root 'src/Flipper.App/Assets/AppIcon.ico'
$stamp = Join-Path $root 'scripts/stamp_icon.py'
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Set-Content -Path (Join-Path $artifacts 'VERSION.txt') -Value $Version -NoNewline

$appCommon = @(
    '-c', 'Release',
    '--self-contained', 'true',
    "/p:Version=$Version",
    "/p:SourceRevisionId=$sourceRevision",
    '/p:PublishSingleFile=false',
    '/p:PublishTrimmed=false',
    '/p:PublishReadyToRun=false'
)

$setupCommon = @(
    '-c', 'Release',
    '--self-contained', 'true',
    "/p:Version=$Version",
    "/p:SourceRevisionId=$sourceRevision",
    '/p:PublishSingleFile=true',
    '/p:PublishTrimmed=false',
    '/p:PublishReadyToRun=false',
    '/p:IncludeNativeLibrariesForSelfExtract=true'
)

function Clear-PublishDirectory([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        return
    }

    $publishRoot = (Resolve-Path -LiteralPath $artifacts).ProviderPath
    $resolved = (Resolve-Path -LiteralPath $path).ProviderPath
    $rootPrefix = $publishRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside the publish root: $resolved"
    }

    # Refuse junctions and symbolic links in the path to the deletion target.
    $ancestor = Get-Item -LiteralPath $resolved -Force
    while ($null -ne $ancestor) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to remove a publish directory through a link: $($ancestor.FullName)"
        }
        $ancestor = $ancestor.Parent
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Sign-ReleaseBinary([string]$path) {
    if ($null -eq $signer) {
        return
    }

    & $SignToolPath sign /fd SHA256 /sha1 $signer /tr $TimestampServer /td SHA256 $path
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool failed for $path (exit $LASTEXITCODE)."
    }
    Assert-ReleaseSignature $path $signer
}

function Publish-App([string]$rid) {
    $out = Join-Path $artifacts $rid
    Clear-PublishDirectory $out
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    & dotnet publish $appProject @appCommon -r $rid -o $out
    if ($LASTEXITCODE -ne 0) {
        throw "publish failed for $rid"
    }
    Copy-VcRuntimeFiles $out $rid (Join-Path $artifacts 'vc-runtime-cache')
    & (Join-Path $PSScriptRoot 'verify-onnx-runtime.ps1') -PublishDirectory $out -RuntimeIdentifier $rid `
        -AssetsPath (Join-Path $root 'src/Flipper.App/obj/project.assets.json')
    $exe = Join-Path $out 'Carousel.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Carousel.exe not found for $rid"
    }
    & python $stamp $exe $icon
    if ($LASTEXITCODE -ne 0) {
        throw "Icon stamping failed for $rid."
    }
    foreach ($name in 'Carousel.exe', 'Carousel.dll', 'Flipper.Core.dll') {
        Sign-ReleaseBinary (Join-Path $out $name)
    }
    $zip = Join-Path $artifacts "Carousel-$rid.zip"
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
}

function Publish-Setup([string]$rid) {
    $out = Join-Path $artifacts "setup/$rid"
    Clear-PublishDirectory $out
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    & dotnet publish $setupProject @setupCommon -r $rid -o $out
    if ($LASTEXITCODE -ne 0) {
        throw "setup publish failed for $rid"
    }
    $exe = Join-Path $out 'Carousel.Setup.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Carousel.Setup.exe not found for $rid"
    }
    Sign-ReleaseBinary $exe
    Copy-Item -LiteralPath $exe -Destination (Join-Path $artifacts "Carousel.Setup-$Version-$rid.exe") -Force
}

Publish-App 'win-x64'
Publish-App 'win-arm64'
Publish-Setup 'win-x64'
Publish-Setup 'win-arm64'
