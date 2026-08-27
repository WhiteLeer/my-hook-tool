param(
    [string]$MuMuRoot,
    [string]$BridgeModule,
    [string]$OutputDirectory,
    [ValidateRange(0, 99)]
    [int]$VmIndex = 0,
    [switch]$NoDialog
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$profilePath = Join-Path $repoRoot 'profiles\HSR-4.4-MUMU.json'
$toolPath = Join-Path $repoRoot 'src\MyHookTool\bin\Release\net8.0\my-hook-tool.exe'
$dotnetPath = Join-Path $repoRoot '..\dotnet\8\dotnet.exe'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'my-hook-tool'
$settingsPath = Join-Path $settingsDirectory 'hsr-4.4-mumu.json'

function Resolve-MuMuRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $candidate = [IO.Path]::GetFullPath($Path)
    if ((Test-Path -LiteralPath $candidate -PathType Leaf) -and
        ([IO.Path]::GetFileName($candidate) -ieq 'MuMuNxMain.exe')) {
        $candidate = Split-Path (Split-Path $candidate -Parent) -Parent
    }
    if ((Test-Path -LiteralPath (Join-Path $candidate 'nx_main\mumu-cli.exe')) -and
        (Test-Path -LiteralPath (Join-Path $candidate 'nx_main\MuMuNxMain.exe'))) {
        return $candidate
    }
    throw "MUMU 路径无效，需要根目录或 nx_main\MuMuNxMain.exe：$Path"
}

function Read-Settings {
    if (!(Test-Path -LiteralPath $settingsPath)) { return $null }
    try { return Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json }
    catch { return $null }
}

function Save-Settings([string]$Mumu, [string]$Bridge, [string]$Output, [int]$Index, [string]$Target) {
    New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
    [ordered]@{
        MuMuRoot = $Mumu
        BridgeModule = $Bridge
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

function Show-ConfigDialog([string]$InitialMumu, [string]$InitialBridge, [string]$InitialOutput, [int]$InitialIndex, [string]$InitialTarget) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()

    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'HSR 4.4 Runtime Hook + MuMu'
    $form.StartPosition = 'CenterScreen'
    $form.AutoScaleMode = 'Dpi'
    $form.MinimumSize = New-Object System.Drawing.Size(820, 390)
    $form.ClientSize = New-Object System.Drawing.Size(900, 440)

    $layout = New-Object System.Windows.Forms.TableLayoutPanel
    $layout.Dock = 'Fill'
    $layout.Padding = New-Object System.Windows.Forms.Padding(14)
    $layout.ColumnCount = 3
    $layout.RowCount = 8
    $layout.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle('Absolute', 150)))
    $layout.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle('Percent', 100)))
    $layout.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle('Absolute', 100)))
    foreach($height in @(42, 42, 42, 42, 42, 42, 36, 52)) {
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
    $bridgeBox = Add-PathRow '桥接 DLL' $InitialBridge '选择运行时桥接 DLL' 'DLL files|*.dll|All files|*.*' $false 2
    $outputBox = Add-PathRow 'Hook 输出目录' $InitialOutput '选择 .hook 输出目录' '' $true 3

    $indexLabel = New-Object System.Windows.Forms.Label
    $indexLabel.Text = 'VM 编号'
    $indexLabel.Anchor = 'Left,Right'
    $indexLabel.TextAlign = 'MiddleLeft'
    $indexBox = New-Object System.Windows.Forms.NumericUpDown
    $indexBox.Minimum = 0
    $indexBox.Maximum = 99
    $indexBox.Value = $InitialIndex
    $indexBox.Width = 100
    $layout.Controls.Add($indexLabel, 0, 4)
    $layout.Controls.Add($indexBox, 1, 4)

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
    $layout.Controls.Add($targetLabel, 0, 5)
    $layout.Controls.Add($targetCombo, 1, 5)
    $layout.Controls.Add($refreshButton, 2, 5)

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
    $hint.Text = '列表来自当前 VM 的 ADB 包名；先停止 VM，再挂起注入宿主并自动启动所选程序。'
    $hint.ForeColor = [System.Drawing.Color]::DimGray
    $hint.AutoSize = $true
    $layout.Controls.Add($hint, 0, 6)
    $layout.SetColumnSpan($hint, 2)

    $start = New-Object System.Windows.Forms.Button
    $start.Text = '开始注入'
    $start.AutoSize = $true
    $start.Anchor = 'Right'
    $start.Add_Click({
        try {
            $resolved = Resolve-MuMuRoot $mumuBox.Text
            if (!(Test-Path -LiteralPath $bridgeBox.Text -PathType Leaf)) { throw '桥接 DLL 不存在。' }
            if ([IO.Path]::GetExtension($bridgeBox.Text) -ine '.dll') { throw '桥接模块必须是 Windows DLL。' }
            if ([string]::IsNullOrWhiteSpace($outputBox.Text)) { throw '必须指定输出目录。' }
            $form.Tag = [pscustomobject]@{
                MuMuRoot = $resolved
                BridgeModule = [IO.Path]::GetFullPath($bridgeBox.Text)
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
    $layout.Controls.Add($buttons, 2, 7)

    $form.AcceptButton = $start
    $form.CancelButton = $cancel
    Update-TargetPackages
    if ($form.ShowDialog() -ne 'OK') { return $null }
    return $form.Tag
}

$settings = Read-Settings
$defaultWorkspace = Join-Path $repoRoot 'artifacts\HookSessions'
$initialMumu = if ($MuMuRoot) { $MuMuRoot } elseif ($settings.MuMuRoot) { $settings.MuMuRoot } else { '' }
$initialBridge = if ($BridgeModule) { $BridgeModule } elseif ($settings.BridgeModule) { $settings.BridgeModule } else { '' }
$initialOutput = if ($OutputDirectory) { $OutputDirectory } elseif ($settings.OutputDirectory) { $settings.OutputDirectory } else { $defaultWorkspace }
$initialIndex = if ($PSBoundParameters.ContainsKey('VmIndex')) { $VmIndex } elseif ($settings.VmIndex -ne $null) { [int]$settings.VmIndex } else { 0 }
$initialTarget = if ($settings.TargetPackage) { [string]$settings.TargetPackage } else { '' }

if (!$NoDialog) {
    $selection = Show-ConfigDialog $initialMumu $initialBridge $initialOutput $initialIndex $initialTarget
    if ($null -eq $selection) { exit 0 }
    $MuMuRoot = $selection.MuMuRoot
    $BridgeModule = $selection.BridgeModule
    $OutputDirectory = $selection.OutputDirectory
    $VmIndex = $selection.VmIndex
    $TargetPackage = $selection.TargetPackage
} else {
    $MuMuRoot = Resolve-MuMuRoot $initialMumu
    if (!(Test-Path -LiteralPath $initialBridge -PathType Leaf)) { throw "桥接 DLL 不存在：$initialBridge" }
    $BridgeModule = [IO.Path]::GetFullPath($initialBridge)
    $OutputDirectory = [IO.Path]::GetFullPath($initialOutput)
    $TargetPackage = $initialTarget
}

Save-Settings (Resolve-MuMuRoot $MuMuRoot) ([IO.Path]::GetFullPath($BridgeModule)) ([IO.Path]::GetFullPath($OutputDirectory)) $VmIndex $TargetPackage
if (!(Test-Path -LiteralPath $toolPath -PathType Leaf)) {
    if (!(Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
        throw "找不到已构建的 my-hook-tool.exe，也找不到 .NET 8：$toolPath"
    }
    $toolInvocation = @($dotnetPath, (Join-Path $repoRoot 'src\MyHookTool\MyHookTool.csproj'))
} else {
    $toolInvocation = @($toolPath)
}

$arguments = @(
    'mumu', '--profile', $profilePath,
    '--mumu-root', (Resolve-MuMuRoot $MuMuRoot),
    '--vmindex', $VmIndex.ToString(),
    '--module', ([IO.Path]::GetFullPath($BridgeModule)),
    '--output', ([IO.Path]::GetFullPath($OutputDirectory)),
    '--watch'
)
if (![string]::IsNullOrWhiteSpace($TargetPackage)) {
    $arguments += @('--launch-package', $TargetPackage)
}
Write-Host '启动 HSR 4.4 MUMU 运行时注入...' -ForegroundColor Cyan
if ($toolInvocation.Count -eq 1) {
    & $toolInvocation[0] @arguments
} else {
    & $toolInvocation[0] $toolInvocation[1] @arguments
}
exit $LASTEXITCODE
