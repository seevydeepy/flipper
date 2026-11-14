param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$AssetsPath
)

$ErrorActionPreference = 'Stop'
$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
$runtime = @($assets.libraries.PSObject.Properties | Where-Object Name -Like 'Microsoft.ML.OnnxRuntime/*')
if ($runtime.Count -ne 1) {
    throw 'The build must resolve one official Microsoft.ML.OnnxRuntime package.'
}
$managedRuntime = $runtime[0].Name.Replace('Microsoft.ML.OnnxRuntime/', 'Microsoft.ML.OnnxRuntime.Managed/')
if ($assets.libraries.PSObject.Properties.Name -notcontains $managedRuntime) {
    throw 'The managed and native ONNX Runtime package versions must match.'
}

$packageDirectories = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
    Join-Path $_ $runtime[0].Value.path
} | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
if ($packageDirectories.Count -eq 0) {
    throw 'The resolved Microsoft.ML.OnnxRuntime package is missing.'
}

foreach ($name in 'onnxruntime.dll', 'onnxruntime_providers_shared.dll') {
    $copies = @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -Filter $name)
    $published = Join-Path $PublishDirectory $name
    if ($copies.Count -ne 1 -or -not (Test-Path -LiteralPath $published -PathType Leaf)) {
        throw "Expected exactly one $name at the publish root for $RuntimeIdentifier."
    }

    $expected = Join-Path $packageDirectories[0] "runtimes/$RuntimeIdentifier/native/$name"
    if ((Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash) {
        throw "$RuntimeIdentifier/$name does not match the resolved official runtime package."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $published
    if ($signature.Status -ne 'Valid' -or
        $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        throw "$RuntimeIdentifier/$name does not have a valid Microsoft signature: $($signature.Status)."
    }
    Write-Host "Verified $RuntimeIdentifier/$name from $($runtime[0].Name): valid Microsoft signature."
}
