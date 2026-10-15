[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$MuMuRoot,
    [Parameter(Mandatory = $true)] [string]$AdbSerial,
    [Parameter(Mandatory = $true)] [string]$Profile,
    [Parameter(Mandatory = $true)] [string]$Output,
    [ValidateRange(1, 65535)] [int]$AdbServerPort = 5038
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$selected = Get-Content -LiteralPath $Profile -Raw | ConvertFrom-Json
if ($selected.experimentalScope -ne 'passive-factory-entry' -or !$selected.factoryEntryProbe.enabled) {
    throw 'An explicit passive-factory-entry profile is required.'
}
$saved = @{}
foreach ($entry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
    if ($entry.Key -match '^(MY_HOOK_|SR_|TYPETREE_RIPPER_|ANDROID_ADB_SERVER_PORT$)') {
        $saved[$entry.Key] = $entry.Value
        [Environment]::SetEnvironmentVariable($entry.Key, $null, 'Process')
    }
}
try {
    $env:ANDROID_ADB_SERVER_PORT = [string]$AdbServerPort
    $env:MY_HOOK_NATIVE_BRIDGE_FORCE_ARM64_LOAD = '1'
    $env:MY_HOOK_NATIVE_BRIDGE_EXTRA_TRIGGER = 'libmain.so'
    $tool = Join-Path $repo 'src/MyHookTool/bin/Release/net8.0/my-hook-tool.exe'
    $adb = Join-Path $MuMuRoot 'nx_main/adb.exe'
    & $adb -P $AdbServerPort connect $AdbSerial | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'ADB connection failed.' }
    & $tool mumu --profile $Profile --mumu-root $MuMuRoot --reuse-mumu --adb-serial $AdbSerial `
        --output $Output --observe-transfers 0 --vtable-probe 0 --enable-native-transfer 0 `
        --native-layout 0 --direct 0 --context-observer 0 --capture-transfer-words 0 `
        --snapshot-bytes 0 --layout-evidence 0 --guest-class-ids 21 --guest-dump-delay-seconds 5 `
        --guest-timeout-seconds 45 --guest-event-seconds 2 --guest-event-max-seconds 2 `
        --guest-event-idle-seconds 1 --guest-total-timeout-seconds 60 --wait-for-unity-runtime 0 --auto-enter 0
    $hostExit = $LASTEXITCODE
    if ($hostExit -ne 0) {
        & $adb -P $AdbServerPort -s $AdbSerial shell am force-stop $selected.androidPackage | Out-Null
        throw "Factory entry experiment failed: $hostExit; the target game was stopped."
    }
    $session = Get-ChildItem -LiteralPath $Output -Directory | Sort-Object Name | Select-Object -Last 1
    $events = @(Get-Content -LiteralPath (Join-Path $session.FullName 'runtime/events.ndjson') | ForEach-Object { $_ | ConvertFrom-Json })
    $fatal = @($events | Where-Object { $_.kind -eq 'factory-entry-rejected' -and $_.originalRestored -eq $false })
    if ($fatal.Count -gt 0) {
        & $adb -P $AdbServerPort -s $AdbSerial shell am force-stop $selected.androidPackage | Out-Null
        throw 'Patch restoration was not verified; the target game process was stopped.'
    }
    $calls = @($events | Where-Object { $_.kind -eq 'factory-entry-call' })
    $terminal = @($events | Where-Object { $_.producer -eq 'GuestFactoryProbe' -and $_.kind -in @('factory-entry-summary','factory-entry-rejected','factory-entry-not-observed') })
    if ($terminal.Count -eq 0) {
        & $adb -P $AdbServerPort -s $AdbSerial shell am force-stop $selected.androidPackage | Out-Null
        throw 'No entry-probe terminal event was collected; the target game was stopped.'
    }
    Write-Host "Natural factory entry records: $($calls.Count). No ABI or asset identity is implied."
} finally {
    foreach ($name in @('ANDROID_ADB_SERVER_PORT', 'MY_HOOK_NATIVE_BRIDGE_FORCE_ARM64_LOAD', 'MY_HOOK_NATIVE_BRIDGE_EXTRA_TRIGGER')) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
