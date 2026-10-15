Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

throw @'
HSR 4.4 仅保留为离线归档，当前不再支持启动或验证 4.4 客体。
请使用 tools\Verify-HSR-4.5-MuMu.ps1，并提供 HSR 4.5 的运行时证据。
'@
