param(
    [Parameter(Mandatory = $true)]
    [string]$AsIndex,
    [Parameter(Mandatory = $true)]
    [string]$TypeTreeReport,
    [string]$Analysis,
    [string]$Profile,
    [string]$Hook,
    [string]$Output,
    [string]$DotnetPath,
    [switch]$RequireFullCatalog
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot

if ([string]::IsNullOrWhiteSpace($Profile)) {
    $Profile = Join-Path $repoRoot 'profiles\HSR-4.5-MUMU.json'
}
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path (Get-Location) 'hsr-4.5-mumu-verification.json'
}
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $bundledDotnet = Join-Path $repoRoot '..\dotnet\8\dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $bundledDotnet) {
        $bundledDotnet
    } else {
        (Get-Command dotnet -ErrorAction Stop).Source
    }
}

$toolPath = Join-Path $repoRoot 'src\MyHookTool\bin\Release\net8.0\my-hook-tool.dll'
if (!(Test-Path -LiteralPath $toolPath -PathType Leaf)) {
    throw "找不到已构建的 my-hook-tool.dll，请先使用 .NET 8 构建：$toolPath"
}

$arguments = @($toolPath, 'verify', '--as-index', $AsIndex,
    '--typetree-report', $TypeTreeReport, '--output', $Output,
    '--profile', $Profile)
foreach ($optional in @(
    @('--analysis', $Analysis),
    @('--hook', $Hook)
)) {
    if (![string]::IsNullOrWhiteSpace($optional[1])) {
        $arguments += $optional
    }
}
if ($RequireFullCatalog) {
    $arguments += '--require-full-catalog'
}

& $DotnetPath @arguments
exit $LASTEXITCODE
