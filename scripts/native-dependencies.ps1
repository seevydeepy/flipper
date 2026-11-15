# Read imports without loading the DLL: this also works for ARM64 on an x64 host.
function Get-NativeBinaryInfo([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
    try {
        $headers = $pe.PEHeaders
        if ($null -eq $headers.PEHeader) { throw "Not a PE executable: $path" }
        function Get-FileOffset([long]$rva) {
            foreach ($section in $headers.SectionHeaders) {
                if ($rva -ge $section.VirtualAddress -and
                    $rva -lt ($section.VirtualAddress + $section.SizeOfRawData)) {
                    return $section.PointerToRawData + $rva - $section.VirtualAddress
                }
            }
            throw "Invalid import address in $path."
        }
        $imports = @(
            foreach ($delayed in $false, $true) {
                $directory = if ($delayed) { $headers.PEHeader.DelayImportTableDirectory }
                             else { $headers.PEHeader.ImportTableDirectory }
                if ($directory.RelativeVirtualAddress -eq 0) { continue }
                $offset = Get-FileOffset $directory.RelativeVirtualAddress
                $entrySize = if ($delayed) { 32 } else { 20 }
                for ($index = 0; $index + $entrySize -le $directory.Size; $index += $entrySize) {
                    $stream.Position = $offset + $index
                    $fields = @(for ($field = 0; $field -lt $entrySize / 4; $field++) { $reader.ReadUInt32() })
                    if (@($fields | Where-Object { $_ -ne 0 }).Count -eq 0) { break }
                    if ($delayed -and $fields[0] -ne 1) { throw "Unsupported delay import format in $path." }
                    $nameRva = if ($delayed) { $fields[1] } else { $fields[3] }
                    $stream.Position = Get-FileOffset $nameRva
                    $name = [Text.StringBuilder]::new()
                    while (($character = $reader.ReadByte()) -ne 0) { [void]$name.Append([char]$character) }
                    $name.ToString()
                }
            }
        )
        [pscustomobject]@{ Machine = [int]$headers.CoffHeader.Machine; Imports = $imports }
    }
    finally { $reader.Dispose(); $pe.Dispose(); $stream.Dispose() }
}

function Assert-NativeDependencies([string]$directory, [string]$rid, [string[]]$roots) {
    $machine = if ($rid -eq 'win-x64') { 0x8664 } else { 0xAA64 }
    # These are Windows components on Carousel's Windows 10 19041+ baseline.
    # Do not resolve imports through PATH/System32: a developer's installed CRT
    # would conceal the very packaging defect this check must catch.
    $windowsDlls = 'kernel32.dll', 'advapi32.dll', 'dbghelp.dll', 'setupapi.dll', 'dxgi.dll'
    $pending = [Collections.Generic.Queue[string]]::new()
    foreach ($root in $roots) { $pending.Enqueue($root) }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($pending.Count -gt 0) {
        $name = $pending.Dequeue()
        if (-not $seen.Add($name)) { continue }
        $path = Join-Path $directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing app-local native dependency for ${rid}: $name"
        }
        $info = Get-NativeBinaryInfo $path
        if ($info.Machine -ne $machine) { throw "Wrong architecture for ${rid}: $name" }
        foreach ($dependency in $info.Imports) {
            if ($dependency -in $windowsDlls -or $dependency -match '^(api-ms-|ext-ms-).+\.dll$') { continue }
            $pending.Enqueue($dependency)
        }
    }
}
