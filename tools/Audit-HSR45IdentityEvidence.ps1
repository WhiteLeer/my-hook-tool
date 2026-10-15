param(
    [Parameter(Mandatory = $true)]
    [string]$Events,
    [Parameter(Mandatory = $true)]
    [string]$RenderDocEvidence,
    [string]$Output = (Join-Path (Get-Location) 'hsr-4.5-identity-evidence.json')
)

$ErrorActionPreference = 'Stop'

function Read-Ndjson([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "找不到 NDJSON 文件：$Path"
    }
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $Path) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            [pscustomobject]@{ Line = $lineNumber; Value = ($line | ConvertFrom-Json) }
        } catch {
            [pscustomobject]@{ Line = $lineNumber; Value = $null }
        }
    }
}

function Get-Value($Object, [string[]]$Names) {
    if ($null -eq $Object) { return $null }
    foreach ($name in $Names) {
        $property = $Object.PSObject.Properties[$name]
        if ($null -ne $property -and $null -ne $property.Value -and
            -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            return $property.Value
        }
    }
    return $null
}

$runtime = @(Read-Ndjson $Events)
$renderDoc = @(Read-Ndjson $RenderDocEvidence)
$drawRecords = @($runtime | Where-Object {
    $kind = [string](Get-Value $_.Value @('kind', 'eventKind'))
    $kind -eq 'guest.vulkan-draw-indexed' -or $kind -eq 'unity.draw-context'
})
$renderEvents = @($renderDoc | Where-Object {
    $eid = Get-Value $_.Value @('eventId', 'renderDocEventId')
    $null -ne $eid
})

$verified = @()
$rejected = @()
foreach ($record in $drawRecords) {
    $value = $record.Value
    $chain = Get-Value $value @('activeTransferCallChain', 'transferContext')
    $shaderSource = Get-Value $value @('shaderSourceFile')
    $shaderPath = Get-Value $value @('shaderPathId', 'shaderPathID')
    $materialSource = Get-Value $value @('materialSourceFile')
    $materialPath = Get-Value $value @('materialPathId', 'materialPathID')
    $eid = Get-Value $value @('renderDocEventId', 'eventId')
    $missing = @()
    if ($null -eq $chain) { $missing += 'activeTransferCallChain' }
    if ($null -eq $shaderSource) { $missing += 'shaderSourceFile' }
    if ($null -eq $shaderPath) { $missing += 'shaderPathId' }
    if ($null -eq $materialSource) { $missing += 'materialSourceFile' }
    if ($null -eq $materialPath) { $missing += 'materialPathId' }
    if ($null -eq $eid) { $missing += 'renderDocEventId' }
    $item = [ordered]@{ line = $record.Line; kind = [string](Get-Value $value @('kind', 'eventKind')); missing = @($missing) }
    if ($missing.Count -eq 0) { $verified += [pscustomobject]$item }
    else { $rejected += [pscustomobject]$item }
}

$status = if ($verified.Count -gt 0 -and $renderEvents.Count -gt 0) { 'verified' } else { 'blocked' }
$report = [ordered]@{
    schema = 'my-hook.identity-evidence-audit.v1'
    status = $status
    policy = 'same-record-only; no name/address/order/ResourceId/hash inference'
    inputs = [ordered]@{
        runtimeEvents = [IO.Path]::GetFullPath($Events)
        renderDocEvidence = [IO.Path]::GetFullPath($RenderDocEvidence)
    }
    counts = [ordered]@{
        runtimeRecords = $runtime.Count
        runtimeDrawCandidates = $drawRecords.Count
        completeSameRecordCandidates = $verified.Count
        rejectedDrawCandidates = $rejected.Count
        renderDocEventRecords = $renderEvents.Count
    }
    verified = @($verified)
    rejected = @($rejected | Select-Object -First 100)
    blockedReason = if ($status -eq 'blocked') {
        '没有观察到同时包含客体 draw、活动 Transfer 上下文、Material/Shader sourceFile#PathID 和 RenderDoc EID 的同一条运行时记录。'
    } else { $null }
}

$parent = Split-Path -Parent ([IO.Path]::GetFullPath($Output))
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Output -Encoding UTF8
Write-Host ("身份证据审计: {0}; 完整同记录: {1}; RenderDoc EID: {2}" -f $status, $verified.Count, $renderEvents.Count)
if ($status -ne 'verified') { exit 2 }
exit 0
