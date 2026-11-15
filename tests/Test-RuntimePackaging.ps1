param(
    [Parameter(Mandatory)][string]$ArtifactsDirectory,
    [Parameter(Mandatory)][string]$AssetsPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$verify = Join-Path $root 'scripts/verify-onnx-runtime.ps1'
. (Join-Path $root 'scripts/native-dependencies.ps1')
$assets = (Get-Item -LiteralPath $AssetsPath).FullName
$artifacts = (Get-Item -LiteralPath $ArtifactsDirectory).FullName
$temporary = Join-Path $artifacts ('.test-runtime-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$checks = 0

function Assert-Rejected([scriptblock]$action, [string]$expected) {
    $failure = $null
    try { & $action | Out-Null } catch { $failure = $_.Exception.Message }
    if (-not $failure -or $failure -notlike "*$expected*") {
        throw "Expected rejection containing '$expected'; got '$failure'."
    }
    $script:checks++
}

try {
    foreach ($rid in 'win-x64', 'win-arm64') {
        [IO.Compression.ZipFile]::ExtractToDirectory(
            (Join-Path $artifacts "Carousel-$rid.zip"), (Join-Path $temporary $rid))
    }
    foreach ($rid in 'win-x64', 'win-arm64') {
        $directory = Join-Path $temporary $rid
        & $verify -PublishDirectory $directory -RuntimeIdentifier $rid -AssetsPath $assets
        $checks++
        # Independent expectations for the supported ONNX builds, not read from
        # the packaging manifest: omitting a manifest entry must not hide a gap.
        $required = @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll')
        if ($rid -eq 'win-x64') { $required += 'vcruntime140_1.dll' }
        foreach ($name in $required) {
            $file = Join-Path $directory $name
            $original = [IO.File]::ReadAllBytes($file)
            try {
                Remove-Item -LiteralPath $file
                Assert-Rejected { & $verify -PublishDirectory $directory -RuntimeIdentifier $rid -AssetsPath $assets } $name
            }
            finally { [IO.File]::WriteAllBytes($file, $original) }
        }

        $file = Join-Path $directory 'msvcp140.dll'
        $original = [IO.File]::ReadAllBytes($file)
        try {
            # A signed runtime for the other architecture is still unusable.
            $other = if ($rid -eq 'win-x64') { 'win-arm64' } else { 'win-x64' }
            Copy-Item -LiteralPath (Join-Path $temporary "$other/msvcp140.dll") -Destination $file -Force
            Assert-Rejected { & $verify -PublishDirectory $directory -RuntimeIdentifier $rid -AssetsPath $assets } 'Wrong architecture'
            [IO.File]::WriteAllBytes($file, $original)
            $modified = [byte[]]$original.Clone()
            $modified[-1] = $modified[-1] -bxor 1
            [IO.File]::WriteAllBytes($file, $modified)
            Assert-Rejected { & $verify -PublishDirectory $directory -RuntimeIdentifier $rid -AssetsPath $assets } 'must match the pinned Microsoft'
        }
        finally { [IO.File]::WriteAllBytes($file, $original) }

        # Follow the CRT's own imports, even without ONNX as the starting DLL.
        try {
            Remove-Item -LiteralPath $file
            Assert-Rejected { Assert-NativeDependencies $directory $rid @('msvcp140_1.dll') } 'MSVCP140.dll'
        }
        finally { [IO.File]::WriteAllBytes($file, $original) }
    }
    Write-Host "Passed $checks native runtime packaging checks against the release ZIPs."
}
finally {
    $resolved = (Get-Item -LiteralPath $temporary -Force)
    $prefix = $artifacts.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        ($resolved.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to remove a test directory outside the artifacts directory: $($resolved.FullName)"
    }
    Remove-Item -LiteralPath $resolved.FullName -Recurse -Force
}
