param(
    [string]$TypeTreeRepository,
    [string]$DotnetPath,
    [string]$CMakePath,
    [switch]$SkipNative
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolRoot = Split-Path -Parent $scriptRoot

function Resolve-Executable([string]$Explicit, [string[]]$Candidates, [string]$Name) {
    $all = @()
    if ($Explicit) { $all += $Explicit }
    $all += $Candidates
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { $all += $command.Source }
    foreach ($candidate in ($all | Where-Object { $_ } | Select-Object -Unique)) {
        try {
            $resolved = [IO.Path]::GetFullPath($candidate)
            if (Test-Path -LiteralPath $resolved -PathType Leaf) { return $resolved }
        } catch { }
    }
    throw "Cannot find $Name. Pass -$Name`Path explicitly."
}

function Resolve-Dotnet8([string]$Explicit) {
    $candidates = @(
        (Join-Path $toolRoot '..\dotnet\8\dotnet.exe'),
        (Join-Path $toolRoot '..\..\dotnet\8\dotnet.exe')
    )
    $dotnet = Resolve-Executable $Explicit $candidates 'dotnet'
    $sdks = @(& $dotnet --list-sdks 2>$null)
    if ($sdks -notmatch '(?m)(^|\s)8\.\d+\.\d+(\s|\[)') {
        throw ".NET 8 SDK is required: $dotnet"
    }
    return $dotnet
}

if (!$TypeTreeRepository) {
    $TypeTreeRepository = Join-Path $toolRoot '..\..\References\TypeTreeRipper'
}
$TypeTreeRepository = [IO.Path]::GetFullPath($TypeTreeRepository)
if (!(Test-Path -LiteralPath (Join-Path $TypeTreeRepository 'CMakePresets.json') -PathType Leaf)) {
    throw "TypeTreeRipper repository is invalid: $TypeTreeRepository"
}

$dotnet = Resolve-Dotnet8 $DotnetPath
$cmake = Resolve-Executable $CMakePath @(
    'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
) 'cmake'

if (!$SkipNative) {
    Write-Host 'Building TypeTreeRipper Android x86_64...' -ForegroundColor Cyan
    Push-Location $TypeTreeRepository
    try {
        & $cmake --build --preset RelWithDebInfoAndroidX8664 --parallel 4
        if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }
        Write-Host 'Building TypeTreeRipper Android ARM64...' -ForegroundColor Cyan
        & $cmake --build --preset RelWithDebInfoAndroidARM64 --parallel 4
        if ($LASTEXITCODE -ne 0) { throw "Native ARM64 build failed: $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}

Write-Host 'Building my-hook-tool with .NET 8...' -ForegroundColor Cyan
$project = Join-Path $toolRoot 'src\MyHookTool\MyHookTool.csproj'
& $dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Managed build failed: $LASTEXITCODE" }

if (!$SkipNative) {
    $sourceLibrary = Join-Path $TypeTreeRepository 'artifacts\RelWithDebInfoAndroidX8664\libTypeTreeRipper.so'
    $sourceArm64Library = Join-Path $TypeTreeRepository 'artifacts\RelWithDebInfoAndroidARM64\libTypeTreeRipper.so'
    if (!(Test-Path -LiteralPath $sourceLibrary -PathType Leaf)) {
        throw "Native output is missing: $sourceLibrary"
    }
    $runtimeDirectory = Join-Path $toolRoot 'runtime\HSR-4.5-MUMU'
    $targetLibrary = Join-Path $runtimeDirectory 'libTypeTreeRipper.so'
    Copy-Item -LiteralPath $sourceLibrary -Destination $targetLibrary -Force
    $arm64TargetLibrary = Join-Path $runtimeDirectory 'libTypeTreeRipper.arm64.so'
    if (Test-Path -LiteralPath $sourceArm64Library -PathType Leaf) {
        Copy-Item -LiteralPath $sourceArm64Library -Destination $arm64TargetLibrary -Force
    }

    $manifestPath = Join-Path $runtimeDirectory 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $libraryHash = (Get-FileHash -LiteralPath $targetLibrary -Algorithm SHA256).Hash
    $arm64LibraryHash = $null
    if (Test-Path -LiteralPath $arm64TargetLibrary -PathType Leaf) {
        $arm64LibraryHash = (Get-FileHash -LiteralPath $arm64TargetLibrary -Algorithm SHA256).Hash
    }
    $revision = (& git -C $TypeTreeRepository rev-parse --short HEAD).Trim()
    & git -C $TypeTreeRepository diff --quiet
    if ($LASTEXITCODE -ne 0) {
        $revision += '+local'
    }
    if (($manifest.sha256 -ne $libraryHash) -or ($manifest.arm64Sha256 -ne $arm64LibraryHash) -or ($manifest.sourceRevision -ne $revision)) {
        $manifest.sha256 = $libraryHash
        $manifest.arm64Sha256 = $arm64LibraryHash
        $manifest.sourceRevision = $revision
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    }
    Write-Host "Runtime deployed: $targetLibrary" -ForegroundColor Green
}

Write-Host 'Build and deployment completed.' -ForegroundColor Green
