[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$MuMuRoot,
    [Parameter(Mandatory = $true)] [string]$AdbSerial,
    [Parameter(Mandatory = $true)] [string]$Profile,
    [Parameter(Mandatory = $true)] [string]$Output,
    [string]$ClassIds,
    [switch]$LoadArm64Companion,
    [ValidateRange(1, 65535)] [int]$AdbServerPort = 5038
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $repo 'src/MyHookTool/bin/Release/net8.0/my-hook-tool.exe'
$selected = Get-Content -LiteralPath $Profile -Raw | ConvertFrom-Json
if ($selected.experimentalScope -ne 'read-only-runtime-catalog') {
    throw 'This runner accepts an explicit read-only-runtime-catalog profile only.'
}
if (!$ClassIds) { $ClassIds = $selected.observeClassIds }
if (!$ClassIds -or $ClassIds -notmatch '^\d+(,\d+){0,7}$') {
    throw 'Select one to eight numeric class IDs for the bounded read-only snapshot.'
}
foreach ($id in ($ClassIds -split ',')) {
    $value = 0
    if (![int]::TryParse($id, [ref]$value) -or $value -lt 0) {
        throw "Invalid class ID: $id"
    }
}
$saved = @{}
# Do not inherit a previous experiment's observers or address overrides.
foreach ($entry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
    if ($entry.Key -match '^(MY_HOOK_|SR_|TYPETREE_RIPPER_|ANDROID_ADB_SERVER_PORT$)') {
        $saved[$entry.Key] = $entry.Value
        [Environment]::SetEnvironmentVariable($entry.Key, $null, 'Process')
    }
}
try {
    $env:ANDROID_ADB_SERVER_PORT = [string]$AdbServerPort
    if ($LoadArm64Companion) {
        if (!$selected.guestArm64RipperPath) { throw 'The selected profile has no ARM64 companion.' }
        $env:MY_HOOK_NATIVE_BRIDGE_FORCE_ARM64_LOAD = '1'
        $env:MY_HOOK_NATIVE_BRIDGE_EXTRA_TRIGGER = 'libmain.so'
    }
    $adb = Join-Path $MuMuRoot 'nx_main/adb.exe'
    & $adb -P $AdbServerPort connect $AdbSerial | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'ADB connection failed.' }
    & $tool mumu --profile $Profile --mumu-root $MuMuRoot --reuse-mumu `
        --adb-serial $AdbSerial --output $Output --observe-transfers 0 --vtable-probe 0 `
        --enable-native-transfer 0 --native-layout 0 --direct 0 --context-observer 0 `
        --capture-transfer-words 0 --snapshot-bytes 0 --layout-evidence 0 `
        --guest-class-ids $ClassIds --guest-dump-delay-seconds 5 --guest-timeout-seconds 45 `
        --guest-event-seconds 2 --guest-event-max-seconds 2 --guest-event-idle-seconds 1 `
        --guest-total-timeout-seconds 60 --wait-for-unity-runtime 0 --auto-enter 0
    if ($LASTEXITCODE -ne 0) { throw "Catalog experiment failed: $LASTEXITCODE" }
} finally {
    [Environment]::SetEnvironmentVariable('ANDROID_ADB_SERVER_PORT', $null, 'Process')
    foreach ($name in $saved.Keys) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
    }
}
