param(
    [string]$RepoRoot,
    [string]$MumuRoot,
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$ProfileId = 'HSR-4.5-MUMU',
    [int]$LatestSessionCount = 5,
    [switch]$Json,
    [switch]$NoWrite
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$profilePath = Join-Path $RepoRoot ("profiles\{0}.json" -f $ProfileId)
$sessionRoot = Join-Path $RepoRoot 'artifacts\HookSessions'
$statusRoot = Join-Path $RepoRoot 'artifacts\status'

function Get-GitStatusSnapshot {
    $branch = ''
    $porcelain = @()
    try {
        $branch = (& git -C $RepoRoot branch --show-current 2>$null).Trim()
        $porcelain = @(git -C $RepoRoot status --porcelain=v1 2>$null)
    } catch { }

    $tracked = @($porcelain | Where-Object { $_ -match '^.. ' -and $_.Substring(0, 2) -notmatch '^\?\?' })
    $untracked = @($porcelain | Where-Object { $_ -match '^\?\? ' })
    [ordered]@{
        branch = $branch
        changedCount = $tracked.Count
        untrackedCount = $untracked.Count
        changedPaths = @($tracked | ForEach-Object { $_.Substring(3) })
        untrackedPaths = @($untracked | ForEach-Object { $_.Substring(3) })
    }
}

function Get-FileSnapshot([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [ordered]@{ exists = $false; path = $Path }
    }
    $item = Get-Item -LiteralPath $Path
    [ordered]@{
        exists = $true
        path = $Path
        bytes = $item.Length
        lastWriteTime = $item.LastWriteTime.ToString('o')
    }
}

function Get-SessionSnapshot([IO.DirectoryInfo]$Session) {
    $hook = Get-ChildItem -LiteralPath $Session.FullName -Filter '*.hook' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    $events = Join-Path $Session.FullName 'runtime\events.ndjson'
    $runtimeRoot = Join-Path $Session.FullName 'runtime'
    $typeTree = if (Test-Path -LiteralPath $runtimeRoot -PathType Container) {
        Get-ChildItem -LiteralPath $runtimeRoot -Recurse -Filter '*.ttbin' -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
    }
    $eventCount = 0
    if (Test-Path -LiteralPath $events -PathType Leaf) {
        $eventCount = @(Get-Content -LiteralPath $events -ErrorAction SilentlyContinue).Count
    }
    [ordered]@{
        name = $Session.Name
        path = $Session.FullName
        lastWriteTime = $Session.LastWriteTime.ToString('o')
        hook = if ($hook) { Get-FileSnapshot $hook.FullName } else { [ordered]@{ exists = $false; path = (Join-Path $Session.FullName '*.hook') } }
        eventCount = $eventCount
        typeTree = if ($typeTree) { Get-FileSnapshot $typeTree.FullName } else { $null }
    }
}

function Get-ProcessSnapshot {
    $names = @('MuMuNxMain', 'MuMuNxDevice', 'MuMuNxSVC', 'qrenderdoc', 'renderdoccmd', 'Unity')
    $items = foreach ($name in $names) {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            [ordered]@{
                name = $process.ProcessName
                id = $process.Id
                workingSetMiB = [math]::Round($process.WorkingSet64 / 1MB, 1)
                privateMemoryMiB = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
                cpuSeconds = if ($process.HasExited) { $null } else { [math]::Round($process.CPU, 1) }
                startTime = try { $process.StartTime.ToString('o') } catch { $null }
            }
        }
    }
    @($items)
}

function Get-AndroidForegroundSnapshot([string]$Root, [string]$PackageName) {
    if ([string]::IsNullOrWhiteSpace($Root) -or [string]::IsNullOrWhiteSpace($PackageName)) {
        return @()
    }
    $adb = Join-Path $Root 'nx_main\adb.exe'
    if (!(Test-Path -LiteralPath $adb -PathType Leaf)) {
        return @([ordered]@{ error = "找不到 ADB: $adb" })
    }
    $devices = @(& $adb devices 2>$null |
        Where-Object { $_ -match '^\S+\s+device$' } |
        ForEach-Object { ($_ -split '\s+')[0] })
    foreach ($device in $devices) {
        $activityLines = @(& $adb -s $device shell dumpsys activity activities 2>$null |
            Select-String 'topResumedActivity|mResumedActivity|mFocusedApp|mCurrentFocus' |
            ForEach-Object { $_.Line.Trim() })
        [ordered]@{
            serial = $device
            package = $PackageName
            packageForeground = @($activityLines | Where-Object { $_ -like "*$PackageName*" }).Count -gt 0
            activityIsSceneEvidence = $false
            sceneEvidenceRule = 'Only runtime events with scene-level types establish Unity scene readiness; Activity name alone is not sufficient.'
            activityLines = $activityLines
        }
    }
}

if ([string]::IsNullOrWhiteSpace($MumuRoot)) {
    $MumuRoot = $env:MY_HOOK_MUMU_ROOT
}
if ([string]::IsNullOrWhiteSpace($MumuRoot)) {
    $siblingMumu = Join-Path (Split-Path -Parent $RepoRoot) 'Games_MUMU\MuMuPlayerGlobal'
    if (Test-Path -LiteralPath $siblingMumu -PathType Container) {
        $MumuRoot = $siblingMumu
    }
}

$latestSessions = @()
if (Test-Path -LiteralPath $sessionRoot -PathType Container) {
    $sessionLimit = [math]::Max(1, $LatestSessionCount)
    # A HookSessions entry is often a named experiment directory containing a
    # second, timestamped session directory. Discover the actual session by
    # its .hook file instead of assuming a fixed nesting depth.
    $sessionDirectories = @(Get-ChildItem -LiteralPath $sessionRoot -Recurse -Filter '*.hook' -File -ErrorAction SilentlyContinue |
        Group-Object DirectoryName |
        ForEach-Object { Get-Item -LiteralPath $_.Name -ErrorAction SilentlyContinue } |
        Where-Object { $_ -and $_.FullName -notmatch '[\\/]_archive([\\/]|$)' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First $sessionLimit)
    $latestSessions = @($sessionDirectories | ForEach-Object { Get-SessionSnapshot $_ })
}

$profile = $null
if (Test-Path -LiteralPath $profilePath -PathType Leaf) {
    try { $profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json } catch { }
}

$state = [ordered]@{
    generatedAt = (Get-Date).ToString('o')
    repoRoot = $RepoRoot
    profile = [ordered]@{
        id = $ProfileId
        path = $profilePath
        exists = Test-Path -LiteralPath $profilePath -PathType Leaf
        package = if ($profile) { $profile.androidPackage } else { $null }
        runtimeLibrary = if ($profile) { $profile.guestRipperPath } else { $null }
    }
    git = Get-GitStatusSnapshot
    processes = Get-ProcessSnapshot
    android = Get-AndroidForegroundSnapshot $MumuRoot $(if ($profile) { $profile.androidPackage } else { $null })
    sessions = $latestSessions
}

if (!$NoWrite) {
    New-Item -ItemType Directory -Force -Path $statusRoot | Out-Null
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $statusRoot 'latest.json') -Encoding UTF8
}

if ($Json) {
    $state | ConvertTo-Json -Depth 8
    exit 0
}

Write-Host "my-hook-tool 状态 | $($state.generatedAt)" -ForegroundColor Cyan
Write-Host "仓库: $RepoRoot"
Write-Host "分支: $($state.git.branch) | 已修改 $($state.git.changedCount) | 未跟踪 $($state.git.untrackedCount)"
Write-Host "Profile: $ProfileId | 存在: $($state.profile.exists) | 包: $($state.profile.package)"

if ($state.android.Count -gt 0) {
    Write-Host 'Android 前台:'
    foreach ($device in $state.android) {
        if ($device.error) {
            Write-Host "  $($device.error)" -ForegroundColor DarkYellow
            continue
        }
        $stateText = if ($device.packageForeground) { '包名在前台' } else { '包名不在前台' }
        Write-Host "  $($device.serial) | $stateText"
        Write-Host '    Activity 仅用于定位进程，不作为 Unity 场景证据'
        foreach ($line in $device.activityLines) { Write-Host "    $line" }
    }
}

$active = @($state.processes)
if ($active.Count -eq 0) {
    Write-Host '进程: 未发现 MUMU / RenderDoc / Unity 进程' -ForegroundColor DarkYellow
} else {
    Write-Host '进程:'
    foreach ($process in $active) {
        Write-Host ("  {0} [{1}] working={2} MiB private={3} MiB" -f $process.name, $process.id, $process.workingSetMiB, $process.privateMemoryMiB)
    }
}

if ($latestSessions.Count -eq 0) {
    Write-Host '最近会话: 无'
} else {
    Write-Host '最近会话:'
    foreach ($session in $latestSessions) {
        $hookState = if ($session.hook.exists) { 'hook=有' } else { 'hook=无' }
        $treeState = if ($session.typeTree -and $session.typeTree.exists) { 'ttbin=有' } else { 'ttbin=无' }
        Write-Host ("  {0} | {1} | events={2} | {3}, {4}" -f $session.name, $session.lastWriteTime, $session.eventCount, $hookState, $treeState)
    }
}

if (!$NoWrite) {
    Write-Host "机器可读状态: $(Join-Path $statusRoot 'latest.json')" -ForegroundColor DarkGray
}
