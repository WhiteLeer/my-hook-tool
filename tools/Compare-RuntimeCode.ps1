param(
    [Parameter(Mandatory)][string]$Events,
    [Parameter(Mandatory)][string]$Library,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
function HexValue([string]$Value) {
    return [Convert]::ToUInt64(($Value -replace '^0x', ''), 16)
}

# Compare captured bytes, not inferred symbols or vtable identities.
$stream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $Library).Path)
$records = [Collections.Generic.List[object]]::new()
$seen = [Collections.Generic.HashSet[string]]::new()
try {
    foreach ($line in [IO.File]::ReadLines((Resolve-Path -LiteralPath $Events).Path)) {
        if (!$line.Contains('"kind":"unity.transfer-layout"')) { continue }
        $event = $line | ConvertFrom-Json
        $windows = [Collections.Generic.List[object]]::new()
        foreach ($code in $event.codeEvidence) { $windows.Add($code) }
        foreach ($slot in $event.vtableSlotCodeEvidence) {
            $windows.Add([pscustomobject]@{
                address = $slot.target; words = $slot.words
                label = 'vtableSlot_' + $slot.offset
            })
            if ($slot.resolvedWords) {
                $windows.Add([pscustomobject]@{
                    address = $slot.resolvedTarget; words = $slot.resolvedWords
                    label = 'vtableSlotResolved_' + $slot.offset
                })
            }
        }
        foreach ($code in $windows) {
            if (!$code.words -or !$code.address) { continue }
            $key = '{0}:{1}:{2}:{3}' -f $event.typeId, $code.address, $code.label, ($code.words -join ',')
            if (!$seen.Add($key)) { continue }
            $address = HexValue $code.address
            $length = $code.words.Count * 4
            $maps = @($event.moduleMaps | Where-Object {
                $_.module -eq [IO.Path]::GetFileName($Library) -and
                $address -ge (HexValue $_.begin) -and
                $address -lt (HexValue $_.end) -and
                $length -le ((HexValue $_.end) - $address)
            })
            $record = [ordered]@{
                typeName = $event.typeName; typeId = $event.typeId
                address = $code.address; label = $code.label
                comparedBytes = 0; status = 'mapping_unavailable'
            }
            $missingWords = @($code.words | Where-Object { $null -eq $_ }).Count
            if ($missingWords -gt 0) {
                $record.status = 'incomplete_capture'
                $record.missingWords = $missingWords
            } elseif ($maps.Count -eq 1) {
                $offset = (HexValue $maps[0].fileOffset) + $address - (HexValue $maps[0].begin)
                $record.fileOffset = '0x{0:x}' -f $offset
                if ($offset -le $stream.Length -and $length -le ($stream.Length - $offset)) {
                    $stream.Position = $offset
                    $bytes = [byte[]]::new($length)
                    $read = 0
                    while ($read -lt $length) {
                        $n = $stream.Read($bytes, $read, $length - $read)
                        if ($n -eq 0) { throw 'Unexpected ELF end of file' }
                        $read += $n
                    }
                    $different = 0
                    for ($i = 0; $i -lt $code.words.Count; $i++) {
                        if ([BitConverter]::ToUInt32($bytes, $i * 4) -ne (HexValue $code.words[$i])) {
                            $different++
                        }
                    }
                    $record.comparedBytes = $length
                    $record.differentWords = $different
                    $record.status = if ($different -eq 0) { 'exact_bytes' } else { 'different_bytes' }
                } else { $record.status = 'outside_file' }
            }
            $records.Add([pscustomobject]$record)
        }
    }
} finally { $stream.Dispose() }

$report = [ordered]@{
    format = 'my-hook.runtime-code-comparison.v1'
    eventsSha256 = (Get-FileHash -LiteralPath $Events -Algorithm SHA256).Hash
    librarySha256 = (Get-FileHash -LiteralPath $Library -Algorithm SHA256).Hash
    scope = 'Captured code windows only; byte equality does not identify function semantics.'
    records = $records.ToArray()
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Output -Encoding UTF8
$records | Group-Object status | Select-Object Name, Count
