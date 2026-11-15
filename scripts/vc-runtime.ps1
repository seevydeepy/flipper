# Only the release CRT files named in the pinned Microsoft package are copied.
# In particular, never redistribute debug_nonredist or copy from another app.
function Get-VcRuntimeManifest {
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'vc-runtime.json') -Raw | ConvertFrom-Json
}

function Copy-VcRuntimeFiles([string]$publishDirectory, [string]$rid, [string]$cacheDirectory) {
    $manifest = Get-VcRuntimeManifest
    $package = $manifest.packages.$rid
    if ($null -eq $package) { throw "No Visual C++ runtime package for $rid." }
    New-Item -ItemType Directory -Force -Path $cacheDirectory | Out-Null
    $archivePath = Join-Path $cacheDirectory "$($package.sha256).vsix"
    if (-not (Test-Path -LiteralPath $archivePath)) {
        Invoke-WebRequest -Uri $package.url -OutFile $archivePath
    }
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $package.sha256) {
        throw "Visual C++ package hash mismatch. Remove $archivePath and retry."
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($file in $package.files.PSObject.Properties) {
            $entry = $archive.GetEntry("$($package.directory)/$($file.Name)")
            if ($null -eq $entry) { throw "Microsoft runtime package is missing $($file.Name)." }
            $destination = Join-Path $publishDirectory $file.Name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $file.Value) {
                throw "Visual C++ runtime hash mismatch: $destination."
            }
        }
    }
    finally { $archive.Dispose() }
}
