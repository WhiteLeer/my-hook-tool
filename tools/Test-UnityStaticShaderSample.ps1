param(
    [Parameter(Mandatory = $true)][string]$UnityExe,
    [Parameter(Mandatory = $true)][string]$Project,
    [Parameter(Mandatory = $true)][string]$Sample,
    [Parameter(Mandatory = $true)][string]$AssetOutput,
    [Parameter(Mandatory = $true)][string]$Report,
    [switch]$SafetyChecks,
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$UnityExe = (Resolve-Path -LiteralPath $UnityExe).Path
$Project = (Resolve-Path -LiteralPath $Project).Path
$Sample = (Resolve-Path -LiteralPath $Sample).Path
$Report = [IO.Path]::GetFullPath($Report)
New-Item -ItemType Directory -Path (Split-Path -Parent $Report) -Force | Out-Null
$editor = Join-Path $Project 'Assets/RenderDocShaderSamples/Editor'
New-Item -ItemType Directory -Path $editor -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../unity/Editor/StaticShaderSampleBuilder.cs') -Destination (Join-Path $editor 'StaticShaderSampleBuilder.cs') -Force
$method = 'StaticShaderSampleBuilder.BuildFromCommandLine'
if ($SafetyChecks) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../unity/Tests/StaticShaderSampleRegression.cs') -Destination (Join-Path $editor 'StaticShaderSampleRegression.cs') -Force
    $method = 'StaticShaderSampleRegression.RunFromCommandLine'
}
$log = [IO.Path]::ChangeExtension($Report, '.log')
$arguments = @('-batchmode', '-force-d3d11', '-job-worker-count', '2', '-assetImportWorkerCount', '1',
    '-projectPath', ('"' + $Project + '"'),
    '-executeMethod', $method,
    '-staticSample', ('"' + $Sample + '"'),
    '-staticSampleOutput', ('"' + $AssetOutput + '"'),
    '-staticSampleReport', ('"' + $Report + '"'),
    '-logFile', ('"' + $log + '"'), '-quit')
# The rendering process owns the shared gate; a parent-held mutex would
# block its own child and would not protect interactive MCP validation.
$start = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    Stop-Process -Id $process.Id -Force
    throw "Only this test Unity instance was stopped after $TimeoutSeconds seconds. Log: $log"
}
$process.Refresh()
if ($process.ExitCode -ne 0) { throw "Unity validation failed ($($process.ExitCode)). Log: $log" }
if (-not (Test-Path -LiteralPath $Report) -or (Get-Item -LiteralPath $Report).LastWriteTimeUtc -lt $start) {
    throw "Unity did not write a fresh validation report. Log: $log"
}
if ($SafetyChecks) {
    $safety = [IO.Path]::ChangeExtension($Report, 'safety.json')
    if (-not (Test-Path -LiteralPath $safety) -or (Get-Item -LiteralPath $safety).LastWriteTimeUtc -lt $start -or
        (Get-Content -LiteralPath $safety -Raw | ConvertFrom-Json).status -ne 'passed') {
        throw "Unity did not write a fresh passing safety report. Log: $log"
    }
}
Get-Content -LiteralPath $Report -Raw
