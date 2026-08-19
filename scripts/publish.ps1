param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Get-NextVersion {
    $parsed = @()
    $tags = & git tag --list 'v*'
    foreach ($tag in $tags) {
        if ($tag -match '^v?(\d+)\.(\d+)\.(\d+)') {
            $parsed += [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
        }
    }

    if ($parsed.Count -eq 0) {
        return '1.0.0'
    }

    $highest = $parsed | Sort-Object | Select-Object -Last 1
    return '{0}.{1}.{2}' -f $highest.Major, $highest.Minor, ($highest.Build + 1)
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-NextVersion
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
    '/p:PublishSingleFile=false',
    '/p:PublishTrimmed=false',
    '/p:PublishReadyToRun=false'
)

$setupCommon = @(
    '-c', 'Release',
    '--self-contained', 'true',
    "/p:Version=$Version",
    '/p:PublishSingleFile=true',
    '/p:PublishTrimmed=false',
    '/p:PublishReadyToRun=false',
    '/p:IncludeNativeLibrariesForSelfExtract=true'
)

function Publish-App([string]$rid) {
    $out = Join-Path $root "artifacts/$rid"
    if (Test-Path $out) {
        Remove-Item $out -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    & dotnet publish $appProject @appCommon -r $rid -o $out
    if ($LASTEXITCODE -ne 0) {
        throw "publish failed for $rid"
    }
    $exe = Join-Path $out 'Carousel.exe'
    if (-not (Test-Path $exe)) {
        throw "Carousel.exe not found for $rid"
    }
    python $stamp $exe $icon
    $zip = Join-Path $artifacts "Carousel-$rid.zip"
    if (Test-Path $zip) {
        Remove-Item $zip -Force
    }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
}

function Publish-Setup([string]$rid) {
    $out = Join-Path $root "artifacts/setup/$rid"
    if (Test-Path $out) {
        Remove-Item $out -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    & dotnet publish $setupProject @setupCommon -r $rid -o $out
    if ($LASTEXITCODE -ne 0) {
        throw "setup publish failed for $rid"
    }
    $exe = Join-Path $out 'Carousel.Setup.exe'
    if (-not (Test-Path $exe)) {
        throw "Carousel.Setup.exe not found for $rid"
    }
    Copy-Item $exe (Join-Path $artifacts "Carousel.Setup-$rid.exe") -Force
}

Publish-App 'win-x64'
Publish-App 'win-arm64'
Publish-Setup 'win-x64'
Publish-Setup 'win-arm64'
