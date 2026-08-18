$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$project = 'src/Flipper.App/Flipper.App.csproj'
$common = @(
    '-c', 'Release',
    '--self-contained', 'true',
    '/p:PublishSingleFile=true',
    '/p:PublishTrimmed=false',
    '/p:PublishReadyToRun=false'
)

function Publish-Rid([string]$rid) {
    $out = Join-Path $root "artifacts/$rid"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    & dotnet publish $project @common -r $rid
    if ($LASTEXITCODE -ne 0) {
        throw "publish failed for $rid"
    }
    $exe = Get-ChildItem -Path (Join-Path $root "src/Flipper.App/bin/Release") -Recurse -Filter Flipper.exe |
        Where-Object { $_.FullName -match [regex]::Escape($rid) } |
        Select-Object -First 1
    if (-not $exe) {
        throw "Flipper.exe not found for $rid"
    }
    Copy-Item $exe.FullName (Join-Path $out 'Flipper.exe') -Force
}

Publish-Rid 'win-x64'
Publish-Rid 'win-arm64'
