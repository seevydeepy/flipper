$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$project = 'src/Flipper.App/Flipper.App.csproj'
$icon = Join-Path $root 'src/Flipper.App/Assets/AppIcon.ico'
$stamp = Join-Path $root 'scripts/stamp_icon.py'
$common = @(
    '-c', 'Release',
    '--self-contained', 'true',
    '/p:PublishSingleFile=false',
    '/p:PublishTrimmed=false',
    '/p:PublishReadyToRun=false'
)

function Publish-Rid([string]$rid) {
    $out = Join-Path $root "artifacts/$rid"
    if (Test-Path $out) {
        Remove-Item $out -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    & dotnet publish $project @common -r $rid -o $out
    if ($LASTEXITCODE -ne 0) {
        throw "publish failed for $rid"
    }
    $exe = Join-Path $out 'Flipper.exe'
    if (-not (Test-Path $exe)) {
        throw "Flipper.exe not found for $rid"
    }
    python $stamp $exe $icon
}

Publish-Rid 'win-x64'
Publish-Rid 'win-arm64'
