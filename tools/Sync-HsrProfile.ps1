[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$MuMuRoot,
    [ValidateRange(0, 99)] [int]$VmIndex = 0,
    [string]$AdbSerial,
    [ValidateRange(1, 65535)] [int]$AdbServerPort = 5037,
    [switch]$UpdateActive,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$adb = Join-Path $MuMuRoot 'nx_main/adb.exe'
$cli = Join-Path $MuMuRoot 'nx_main/mumu-cli.exe'
if (!(Test-Path -LiteralPath $adb) -or !(Test-Path -LiteralPath $cli)) {
    throw 'MuMuRoot must contain nx_main/adb.exe and nx_main/mumu-cli.exe.'
}
if (!$AdbSerial) {
    $info = (& $cli info -v $VmIndex | Out-String | ConvertFrom-Json)
    if (!$info.is_android_started -or !$info.adb_port) {
        throw 'The selected VM is not Android-ready. This command does not start the VM.'
    }
    $AdbSerial = '{0}:{1}' -f $info.adb_host_ip, $info.adb_port
}
& $adb -P $AdbServerPort connect $AdbSerial | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'ADB connection failed.' }
$metadata = & $adb -P $AdbServerPort -s $AdbSerial shell dumpsys package com.miHoYo.hkrpg | Out-String
if ($LASTEXITCODE -ne 0 -or $metadata -notmatch '(?m)^\s*versionName=(\d+)\.(\d+)\.([^\s]+)') {
    throw 'Could not read the actual installed HSR package version.'
}
$version = '{0}.{1}' -f $Matches[1], $Matches[2]
$clientVersion = '{0}.{1}.{2}' -f $Matches[1], $Matches[2], $Matches[3]
if ($metadata -notmatch '(?m)^\s*versionCode=(\d+)') { throw 'Missing package versionCode.' }
$versionCode = [int]$Matches[1]
$profileId = 'HSR-{0}-MUMU' -f $version
$profiles = Join-Path $repoRoot 'profiles'
$profilePath = Join-Path $profiles ($profileId + '.json')
$created = $false
if (Test-Path -LiteralPath $profilePath) {
    $profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
    if ($profile.game -ne 'HSR' -or $profile.version -ne $version) {
        throw 'The existing profile has conflicting game/version metadata.'
    }
    if (($profile.observedClientVersion -and $profile.observedClientVersion -ne $clientVersion) -or
        ($profile.runtimeValidation.installedVersionCode -and
         $profile.runtimeValidation.installedVersionCode -ne $versionCode)) {
        throw 'The package build changed within this version. Preserve the previous ABI evidence and validate a separate build profile before updating the active target.'
    }
} else {
    $profile = [ordered]@{
        id = $profileId
        game = 'HSR'
        version = $version
        observedClientVersion = $clientVersion
        androidPackage = 'com.miHoYo.hkrpg'
        mumuAndroidVersion = 'auto'
        runtimeSupport = 'discovery-only'
        captureExtensions = @('.srrdc')
        alignmentPolicy = 'explicit-only'
        runtimeValidation = [ordered]@{
            status = 'pending'
            installedVersionCode = $versionCode
            verifiedNativeClassIds = @()
        }
        notes = @('Discovered from the installed package. Native offsets, ABI and TypeTree require independent validation.')
    }
    if ($UpdateActive) {
        $profile | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $profilePath -Encoding UTF8
        $created = $true
    }
}
$report = [ordered]@{
    schema = 'my-hook.client-version.v1'
    observedUtc = [DateTime]::UtcNow.ToString('o')
    package = 'com.miHoYo.hkrpg'
    clientVersion = $clientVersion
    versionCode = $versionCode
    profile = $profileId
    runtimeSupport = $profile.runtimeSupport
    createdProfile = $created
    activeUpdated = [bool]$UpdateActive
}
if ($UpdateActive) {
    [ordered]@{
        schema = 'my-hook.active-target.v1'
        profile = $profileId
        version = $version
        validation = $profile.runtimeSupport
        updated = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profiles 'active-hsr.json') -Encoding UTF8
}
if ($Output) {
    $absolute = [IO.Path]::GetFullPath($Output)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolute)) | Out-Null
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $absolute -Encoding UTF8
}
$report | ConvertTo-Json -Depth 10
