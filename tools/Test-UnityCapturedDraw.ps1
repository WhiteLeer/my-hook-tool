param(
    [Parameter(Mandatory = $true)][string]$UnityExe,
    [Parameter(Mandatory = $true)][string]$Project,
    [Parameter(Mandatory = $true)][string]$Manifest,
    [string]$AssetOutput = 'Assets/Sample',
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$UnityExe = (Resolve-Path -LiteralPath $UnityExe).Path
$Project = (Resolve-Path -LiteralPath $Project).Path
$Manifest = (Resolve-Path -LiteralPath $Manifest).Path
$reportDirectory = Split-Path -Parent $Manifest
$report = Join-Path $reportDirectory 'unity-validation.json'
$log = Join-Path $reportDirectory 'unity-probe.log'
$source = Join-Path $PSScriptRoot '../unity/Editor/CapturedShaderSampleBuilder.cs'
$editor = Join-Path $Project 'Assets/Editor'
New-Item -ItemType Directory -Path $editor -Force | Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $editor 'CapturedShaderSampleBuilder.cs') -Force
$start = [DateTime]::UtcNow
$arguments = @('-batchmode', '-force-d3d11', '-job-worker-count', '2', '-assetImportWorkerCount', '1',
    '-projectPath', ('"' + $Project + '"'),
    '-executeMethod', 'CapturedShaderSampleBuilder.ImportFromCommandLine',
    '-rdcSampleManifest', ('"' + $Manifest + '"'),
    '-rdcSampleOutput', ('"' + $AssetOutput + '"'),
    '-rdcSampleReport', ('"' + $report + '"'),
    '-logFile', ('"' + $log + '"'), '-quit')
$mutex = [Threading.Mutex]::new($false, 'Local\MyHookGpuValidation')
$locked = $false
try {
    try { $locked = $mutex.WaitOne(30000) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another Hook GPU replay or Unity validation is still running.' }
    $start = [DateTime]::UtcNow
    $process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force
        throw "Only the test Unity instance was stopped after $TimeoutSeconds seconds. Log: $log"
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw "Unity test failed ($($process.ExitCode)). Log: $log" }
    if (-not (Test-Path -LiteralPath $report) -or (Get-Item -LiteralPath $report).LastWriteTimeUtc -lt $start) {
        throw "Unity did not write a fresh validation report. Log: $log"
    }
    Get-Content -LiteralPath $report -Raw
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
