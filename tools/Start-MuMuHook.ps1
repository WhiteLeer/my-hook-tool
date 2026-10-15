param(
    [string]$MuMuRoot,
    [string]$OutputDirectory,
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$ProfileId,
    [ValidateRange(0, 99)]
    [int]$VmIndex = 0,
    [string]$DotnetPath,
    [switch]$ObserveTransfers,
    [switch]$ReuseMumu,
    [switch]$NoDialog
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$ProfileId = if ($ProfileId) { $ProfileId } elseif ($env:MY_HOOK_PROFILE) { $env:MY_HOOK_PROFILE } else {
    $activeProfilePath = Join-Path $repoRoot 'profiles\active-hsr.json'
    if (!(Test-Path -LiteralPath $activeProfilePath -PathType Leaf)) {
        throw '未指定 Profile，且缺少 profiles/active-hsr.json。'
    }
    (Get-Content -LiteralPath $activeProfilePath -Raw | ConvertFrom-Json).profile
}
$profilePath = Join-Path $repoRoot ("profiles\{0}.json" -f $ProfileId)
if (!(Test-Path -LiteralPath $profilePath -PathType Leaf)) {
    throw "找不到 Profile：$profilePath。请将对应的 JSON 放入仓库 profiles 目录。"
}
$toolPath = Join-Path $repoRoot 'src\MyHookTool\bin\Release\net8.0\my-hook-tool.exe'
$dotnetPath = $null
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'my-hook-tool'
$settingsPath = Join-Path $settingsDirectory (("{0}.json" -f $ProfileId.ToLowerInvariant()))

function Resolve-Dotnet8 {
    $candidates = @()
    if ($DotnetPath) { $candidates += $DotnetPath }
    if ($env:MY_HOOK_DOTNET) { $candidates += $env:MY_HOOK_DOTNET }
    $candidates += @(
        (Join-Path $repoRoot 'dotnet\8\dotnet.exe'),
        (Join-Path $repoRoot '..\dotnet\8\dotnet.exe')
    )
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }

    foreach ($candidate in ($candidates | Where-Object { $_ } | Select-Object -Unique)) {
        $resolved = [IO.Path]::GetFullPath($candidate)
        if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) { continue }
        $sdks = @(& $resolved --list-sdks 2>$null)
        if ($sdks -match '(?m)(^|\s)8\.\d+\.\d+(\s|\[)') { return $resolved }
    }
    throw '找不到 .NET 8 SDK。请设置 MY_HOOK_DOTNET，或将 .NET 8 放入仓库的 dotnet\8 / 仓库同级 dotnet\8。不会静默使用 .NET 9/10。'
}

function Resolve-MuMuRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $candidate = [IO.Path]::GetFullPath($Path)
    if ((Test-Path -LiteralPath $candidate -PathType Leaf) -and
        ([IO.Path]::GetFileName($candidate) -ieq 'MuMuNxMain.exe' -or
         [IO.Path]::GetFileName($candidate) -ieq 'mumu-cli.exe')) {
        $candidate = Split-Path (Split-Path $candidate -Parent) -Parent
    } elseif ((Test-Path -LiteralPath $candidate -PathType Container) -and
        ([IO.Path]::GetFileName($candidate) -ieq 'nx_main')) {
        $candidate = Split-Path $candidate -Parent
    }
    if ((Test-Path -LiteralPath (Join-Path $candidate 'nx_main\mumu-cli.exe')) -and
        (Test-Path -LiteralPath (Join-Path $candidate 'nx_main\MuMuNxMain.exe'))) {
        return $candidate
    }
    throw "MUMU 路径无效，需要根目录或 nx_main\MuMuNxMain.exe：$Path"
}

function Try-ResolveMuMuRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try { return Resolve-MuMuRoot $Path } catch { return $null }
}

function Find-RunningMuMuRoot {
    try {
        $processes = @(Get-Process -Name 'MuMuNxMain' -ErrorAction SilentlyContinue)
        foreach ($process in $processes) {
            $path = $process.Path
            $resolved = Try-ResolveMuMuRoot $path
            if ($resolved) { return $resolved }
        }
    } catch { }
    return $null
}

function Find-InstalledMuMuRoot {
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($env:MY_MUMU_ROOT) { $candidates.Add($env:MY_MUMU_ROOT) }

    # Keep the portable workspace layout usable after moving the whole workspace
    # to another drive without baking that drive letter into the launcher.
    $candidates.Add((Join-Path $repoRoot '..\..\Games_MUMU\MuMuPlayerGlobal'))
    $candidates.Add((Join-Path $repoRoot '..\..\Games_MUMU\MuMuPlayerGlobal-12.0'))

    $registryPaths = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($registryPath in $registryPaths) {
        try {
            foreach ($entry in @(Get-ItemProperty $registryPath -ErrorAction SilentlyContinue)) {
                if ($entry.DisplayName -match '(?i)MuMu') {
                    if ($entry.InstallLocation) { $candidates.Add([string]$entry.InstallLocation) }
                    if ($entry.DisplayIcon) { $candidates.Add(([string]$entry.DisplayIcon -split ',')[0]) }
                }
            }
        } catch { }
    }

    $programFiles = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA) |
        Where-Object { $_ } |
        Select-Object -Unique
    foreach ($base in $programFiles) {
        $candidates.Add((Join-Path $base 'Netease\MuMuPlayerGlobal-12.0'))
        $candidates.Add((Join-Path $base 'Netease\MuMuPlayerGlobal'))
        $candidates.Add((Join-Path $base 'MuMuPlayerGlobal-12.0'))
        $candidates.Add((Join-Path $base 'MuMuPlayerGlobal'))
    }

    foreach ($candidate in $candidates | Where-Object { $_ } | Select-Object -Unique) {
        $resolved = Try-ResolveMuMuRoot $candidate
        if ($resolved) { return $resolved }
    }
    return $null
}

function Read-Settings {
    if (!(Test-Path -LiteralPath $settingsPath)) { return $null }
    try { return Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json }
    catch { return $null }
}

function Try-ResolveExistingDirectory([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try {
        $resolved = [IO.Path]::GetFullPath($Path)
        if (Test-Path -LiteralPath $resolved -PathType Container) { return $resolved }
    } catch { }
    return $null
}

function Save-Settings([string]$Mumu, [string]$Output, [int]$Index, [string]$Target) {
    New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
    [ordered]@{
        MuMuRoot = $Mumu
        OutputDirectory = $Output
        VmIndex = $Index
        TargetPackage = $Target
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8
}

function Get-MuMuPackages([string]$Root, [int]$Index) {
    $cli = Join-Path $Root 'nx_main\mumu-cli.exe'
    if (!(Test-Path -LiteralPath $cli -PathType Leaf)) { return @() }
    try {
        $lines = @(& $cli adb --vmindex $Index --cmd 'shell pm list packages -3' 2>$null)
        $packages = foreach ($line in $lines) {
            if ($line -match '^\s*package:(\S+)') { $Matches[1] }
        }
        return @($packages | Sort-Object -Unique | ForEach-Object {
            [pscustomobject]@{ Display = $_; Package = $_ }
        })
    } catch { return @() }
}

function Show-ConfigDialog([string]$InitialMumu, [string]$InitialOutput, [int]$InitialIndex, [string]$InitialTarget) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "{0} Runtime Hook + MuMu" -f $ProfileId
    $form.StartPosition = 'CenterScreen'
    $form.AutoScaleMode = 'Dpi'
    $form.MinimumSize = New-Object System.Drawing.Size(820, 390)
    $form.ClientSize = New-Object System.Drawing.Size(900, 440)

    $layout = New-Object System.Windows.Forms.TableLayoutPanel
    $layout.Dock = 'Fill'
    $layout.Padding = New-Object System.Windows.Forms.Padding(14)
    $layout.ColumnCount = 3
    $layout.RowCount = 7
    $layout.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle('Absolute', 150)))
    $layout.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle('Percent', 100)))
    $layout.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle('Absolute', 100)))
    foreach($height in @(42, 42, 42, 42, 42, 36, 52)) {
        $layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle('Absolute', $height)))
    }
    $form.Controls.Add($layout)

    $title = New-Object System.Windows.Forms.Label
    $title.Text = '运行时识别注入'
    $title.Font = New-Object System.Drawing.Font('Segoe UI', 12, [System.Drawing.FontStyle]::Bold)
    $title.AutoSize = $true
    $layout.Controls.Add($title, 0, 0)
    $layout.SetColumnSpan($title, 3)

    function Add-PathRow([string]$LabelText, [string]$Value, [string]$DialogTitle, [string]$Filter, [bool]$Folder, [int]$Row) {
        $label = New-Object System.Windows.Forms.Label
        $label.Text = $LabelText
        $label.Anchor = 'Left,Right'
        $label.TextAlign = 'MiddleLeft'
        $box = New-Object System.Windows.Forms.TextBox
        $box.Text = $Value
        $box.Dock = 'Fill'
        $button = New-Object System.Windows.Forms.Button
        $button.Text = '浏览...'
        $button.Dock = 'Fill'
        $button.Add_Click({
            if ($Folder) {
                $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
                $dialog.Description = $DialogTitle
                if (Test-Path -LiteralPath $box.Text -PathType Container) { $dialog.SelectedPath = $box.Text }
                if ($dialog.ShowDialog() -eq 'OK') { $box.Text = $dialog.SelectedPath }
            } else {
                $dialog = New-Object System.Windows.Forms.OpenFileDialog
                $dialog.Title = $DialogTitle
                $dialog.Filter = $Filter
                if (Test-Path -LiteralPath $box.Text -PathType Leaf) { $dialog.FileName = $box.Text }
                if ($dialog.ShowDialog() -eq 'OK') { $box.Text = $dialog.FileName }
            }
        })
        $layout.Controls.Add($label, 0, $Row)
        $layout.Controls.Add($box, 1, $Row)
        $layout.Controls.Add($button, 2, $Row)
        return $box
    }

    $mumuBox = Add-PathRow 'MUMU 宿主' $InitialMumu '选择 MuMuNxMain.exe 或 MUMU 根目录' 'MuMuNxMain.exe|MuMuNxMain.exe|Executable files|*.exe' $false 1
    $outputBox = Add-PathRow 'Hook 输出目录' $InitialOutput '选择 .hook 输出目录' '' $true 2

    $indexLabel = New-Object System.Windows.Forms.Label
    $indexLabel.Text = 'VM 编号'
    $indexLabel.Anchor = 'Left,Right'
    $indexLabel.TextAlign = 'MiddleLeft'
    $indexBox = New-Object System.Windows.Forms.NumericUpDown
    $indexBox.Minimum = 0
    $indexBox.Maximum = 99
    $indexBox.Value = $InitialIndex
    $indexBox.Width = 100
    $layout.Controls.Add($indexLabel, 0, 3)
    $layout.Controls.Add($indexBox, 1, 3)

    $targetLabel = New-Object System.Windows.Forms.Label
    $targetLabel.Text = '目标程序'
    $targetLabel.Anchor = 'Left,Right'
    $targetLabel.TextAlign = 'MiddleLeft'
    $targetCombo = New-Object System.Windows.Forms.ComboBox
    $targetCombo.Dock = 'Fill'
    $targetCombo.DropDownStyle = 'DropDownList'
    $targetCombo.DisplayMember = 'Display'
    $refreshButton = New-Object System.Windows.Forms.Button
    $refreshButton.Text = '刷新'
    $refreshButton.Dock = 'Fill'
    $layout.Controls.Add($targetLabel, 0, 4)
    $layout.Controls.Add($targetCombo, 1, 4)
    $layout.Controls.Add($refreshButton, 2, 4)

    function Update-TargetPackages {
        $targetCombo.Items.Clear()
        [void]$targetCombo.Items.Add([pscustomobject]@{ Display = '[不自动启动]'; Package = '' })
        try {
            $root = Resolve-MuMuRoot $mumuBox.Text
            $items = @(Get-MuMuPackages $root ([int]$indexBox.Value))
            foreach ($item in $items) { [void]$targetCombo.Items.Add($item) }
            if ($items.Count -eq 0) {
                [void]$targetCombo.Items.Add([pscustomobject]@{ Display = '[未读取到应用，请启动 VM 后刷新]'; Package = '' })
            }
        } catch {
            [void]$targetCombo.Items.Add([pscustomobject]@{ Display = '[MUMU 未运行或 ADB 不可用]'; Package = '' })
        }
        $targetCombo.SelectedIndex = 0
        if (![string]::IsNullOrWhiteSpace($InitialTarget)) {
            for ($i = 0; $i -lt $targetCombo.Items.Count; $i++) {
                if ($targetCombo.Items[$i].Package -eq $InitialTarget) {
                    $targetCombo.SelectedIndex = $i
                    break
                }
            }
        }
    }
    $refreshButton.Add_Click({ Update-TargetPackages })
    $mumuBox.Add_Leave({ try { Update-TargetPackages } catch {} })
    $indexBox.Add_ValueChanged({ try { Update-TargetPackages } catch {} })

    $hint = New-Object System.Windows.Forms.Label
    $hint.Text = '列表来自当前 VM 的 ADB 包名；HSR Profile 会在 Unity 登录页自动点击一次，流程总时限约 120 秒。'
    $hint.ForeColor = [System.Drawing.Color]::DimGray
    $hint.AutoSize = $true
    $layout.Controls.Add($hint, 0, 5)
    $layout.SetColumnSpan($hint, 2)

    $start = New-Object System.Windows.Forms.Button
    $start.Text = '开始注入'
    $start.AutoSize = $true
    $start.Anchor = 'Right'
    $start.Add_Click({
        try {
            $resolved = Resolve-MuMuRoot $mumuBox.Text
            if ([string]::IsNullOrWhiteSpace($outputBox.Text)) { throw '必须指定输出目录。' }
            $form.Tag = [pscustomobject]@{
                MuMuRoot = $resolved
                OutputDirectory = [IO.Path]::GetFullPath($outputBox.Text)
                VmIndex = [int]$indexBox.Value
                TargetPackage = [string]$targetCombo.SelectedItem.Package
            }
            $form.DialogResult = 'OK'
            $form.Close()
        } catch { [System.Windows.Forms.MessageBox]::Show($form, $_.Exception.Message, '路径错误', 'OK', 'Error') | Out-Null }
    })
    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = '取消'
    $cancel.AutoSize = $true
    $cancel.Anchor = 'Right'
    $cancel.Add_Click({ $form.DialogResult = 'Cancel'; $form.Close() })
    $buttons = New-Object System.Windows.Forms.FlowLayoutPanel
    $buttons.FlowDirection = 'RightToLeft'
    $buttons.Dock = 'Fill'
    $buttons.Controls.Add($cancel)
    $buttons.Controls.Add($start)
    $layout.Controls.Add($buttons, 2, 6)

    $form.AcceptButton = $start
    $form.CancelButton = $cancel
    Update-TargetPackages
    if ($form.ShowDialog() -ne 'OK') { return $null }
    return $form.Tag
}

$settings = Read-Settings
$launcherStartedAt = Get-Date
$defaultWorkspace = Join-Path $repoRoot 'artifacts\HookSessions'
$rememberedMumu = if ($settings) { [string]$settings.MuMuRoot } else { '' }
$initialMumu = if (Try-ResolveMuMuRoot $MuMuRoot) {
    Try-ResolveMuMuRoot $MuMuRoot
} elseif (Try-ResolveMuMuRoot $rememberedMumu) {
    Try-ResolveMuMuRoot $rememberedMumu
} else {
    $runningMumu = Find-RunningMuMuRoot
    if ($runningMumu) { $runningMumu } else { Find-InstalledMuMuRoot }
}
$initialMumu = if ($initialMumu) { $initialMumu } else { '' }
$rememberedOutput = if ($settings) { Try-ResolveExistingDirectory ([string]$settings.OutputDirectory) } else { $null }
$initialOutput = if ($OutputDirectory) { $OutputDirectory } elseif ($rememberedOutput) { $rememberedOutput } else { $defaultWorkspace }
$initialIndex = if ($PSBoundParameters.ContainsKey('VmIndex')) { $VmIndex } elseif ($settings.VmIndex -ne $null) { [int]$settings.VmIndex } else { 0 }
$initialTarget = if ($settings.TargetPackage) { [string]$settings.TargetPackage } else { '' }

if (!$NoDialog) {
    $selection = Show-ConfigDialog $initialMumu $initialOutput $initialIndex $initialTarget
    if ($null -eq $selection) { exit 0 }
    $MuMuRoot = $selection.MuMuRoot
    $OutputDirectory = $selection.OutputDirectory
    $VmIndex = $selection.VmIndex
    $TargetPackage = $selection.TargetPackage
} else {
    $MuMuRoot = Resolve-MuMuRoot $initialMumu
    $OutputDirectory = [IO.Path]::GetFullPath($initialOutput)
    $TargetPackage = $initialTarget
}

Save-Settings (Resolve-MuMuRoot $MuMuRoot) ([IO.Path]::GetFullPath($OutputDirectory)) $VmIndex $TargetPackage
Write-Host ("[1/4] 配置已解析并保存。耗时 {0:n1}s。" -f ((Get-Date) - $launcherStartedAt).TotalSeconds) -ForegroundColor DarkGray
if (!(Test-Path -LiteralPath $toolPath -PathType Leaf)) {
    $dotnetPath = Resolve-Dotnet8
    $toolInvocation = @($dotnetPath, (Join-Path $repoRoot 'src\MyHookTool\MyHookTool.csproj'))
} else {
    $toolInvocation = @($toolPath)
}

$arguments = @(
    'mumu', '--profile', $profilePath,
    '--mumu-root', (Resolve-MuMuRoot $MuMuRoot),
    '--vmindex', $VmIndex.ToString(),
    '--output', ([IO.Path]::GetFullPath($OutputDirectory))
)
if (![string]::IsNullOrWhiteSpace($TargetPackage)) {
    $arguments += @('--launch-package', $TargetPackage)
}
if ($ObserveTransfers) {
    $arguments += @('--observe-transfers', '1')
}
if ($ReuseMumu) {
    $arguments += '--reuse-mumu'
}
Write-Host ("[2/4] 启动 {0} 运行时注入..." -f $ProfileId) -ForegroundColor Cyan
Write-Host ("[3/4] 目标: VM {0}, 包 {1}; 输出: {2}" -f $VmIndex, $(if ($TargetPackage) { $TargetPackage } else { '[不自动启动]' }), ([IO.Path]::GetFullPath($OutputDirectory))) -ForegroundColor DarkGray
$toolStartedAt = Get-Date
if ($toolInvocation.Count -eq 1) {
    & $toolInvocation[0] @arguments
} else {
    & $toolInvocation[0] $toolInvocation[1] @arguments
}
$exitCode = $LASTEXITCODE
$elapsedSeconds = [math]::Round(((Get-Date) - $toolStartedAt).TotalSeconds, 1)
$summaryPath = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'launcher-summary.json'
$summary = [ordered]@{
    format = 'my-hook.launcher-summary.v1'
    profile = $ProfileId
    mumuRoot = [IO.Path]::GetFullPath($MuMuRoot)
    vmIndex = $VmIndex
    targetPackage = $TargetPackage
    startedAt = $toolStartedAt.ToUniversalTime().ToString('o')
    finishedAt = (Get-Date).ToUniversalTime().ToString('o')
    elapsedSeconds = $elapsedSeconds
    exitCode = $exitCode
    workflow = 'profile-driven-auto-enter-and-scene-bounded-collection'
    timeoutPolicy = 'profile guestTotalTimeoutSeconds is a hard upper bound; successful reuse runs should finish within 60-120 seconds'
}
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetFullPath($OutputDirectory)) | Out-Null
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Host ("[4/4] 运行时流程结束，退出码: {0}，耗时: {1:n1}s。摘要: {2}" -f $exitCode, $elapsedSeconds, $summaryPath) -ForegroundColor DarkGray
exit $exitCode
