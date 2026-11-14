function ConvertTo-SignerThumbprint([string]$thumbprint) {
    $normalised = $thumbprint -replace '\s', ''
    if ($normalised -notmatch '^[0-9a-f]{40}$') {
        throw 'Expected a 40-character hexadecimal signing certificate thumbprint.'
    }
    return $normalised
}

function Assert-ReleaseSignature([string]$path, [string]$expectedThumbprint) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing release binary: $path"
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $expectedThumbprint -or
        $null -eq $signature.TimeStamperCertificate) {
        throw "Expected a valid timestamped signature from $expectedThumbprint on $path (signature status: $($signature.Status))."
    }
}
