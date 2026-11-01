#Requires -Version 5.1
<#
.SYNOPSIS
    自动把手机连到「Bluetooth Audio Receiver」（自编版）的 A2DP 接收端。

.DESCRIPTION
    2026-10-27 变更：商店版（55746MarkSmirnov.BluetoothAudioReveicer）已卸载，
    现在只保留自编版 `E:\Apps\BluetoothAudioReceiver\BluetoothAudioReceiver.exe`。

    为什么还需要这个脚本：
      · Windows 本机没有内建 A2DP 接收服务（classic 服务枚举 rc=1060），
        所以设置页里点那台手机的"连接"是没反应的 —— 没有可连的端点。
      · 端点只有这个 App 运行时才存在。
      · 自编版比商店版好：启动后会自动选中已配对设备并直接进入接收状态；
        但设备断线后仍需要点一次「Verbinden / Connect」。

    本脚本做：启动 App → 等窗口 → 若未连接则选中设备并点连接 → 轮询确认。

.PARAMETER DeviceName
    设备名子串匹配，默认 REDMI。
.PARAMETER Status
    只看状态，不做任何操作。
.PARAMETER NoLaunch
    不主动启动 App。
.PARAMETER TimeoutSeconds
    等待连接建立的最长秒数，默认 15。
.PARAMETER ExePath
    自编版可执行文件路径。
#>
[CmdletBinding()]
param(
    [string]$DeviceName = 'REDMI',
    [switch]$Status,
    [switch]$NoLaunch,
    [int]$TimeoutSeconds = 15,
    [string]$ExePath = 'E:\Apps\BluetoothAudioReceiver\BluetoothAudioReceiver.exe'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$WinTitle = 'Bluetooth Audio Receiver'

# 按钮名随语言变：德语 Verbinden/Trennen、英语 Connect/Disconnect、中文 连接/断开
$ConnectNames    = @('Verbinden', 'Connect', '连接')
$DisconnectNames = @('Trennen', 'Disconnect', '断开')
$ReceivingPattern = '正在接收|Receiving|已连接'

function Write-Step { param([string]$Msg) Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Msg) }

function Get-AppWindow {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $WinTitle)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}

function Get-Buttons {
    param($Window)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    return $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-ButtonByName {
    param($Window, [string[]]$Names)
    foreach ($b in (Get-Buttons -Window $Window)) {
        foreach ($n in $Names) { if ($b.Current.Name -eq $n) { return $b } }
    }
    return $null
}

function Get-Texts {
    param($Window)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $out = @()
    foreach ($t in $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        $out += $t.Current.Name
    }
    return $out
}

function Test-Connected {
    param($Window)
    foreach ($t in (Get-Texts -Window $Window)) {
        if ($t -match $ReceivingPattern) { return $true }
    }
    $dis = Get-ButtonByName -Window $Window -Names $DisconnectNames
    if ($dis -and $dis.Current.IsEnabled) { return $true }
    return $false
}

# ---------------------------------------------------------------- 只看状态

if ($Status) {
    $w = Get-AppWindow
    if (-not $w) { Write-Host 'App 未运行'; exit 1 }
    $conn = Test-Connected -Window $w
    Write-Host ("连接状态: {0}" -f $(if ($conn) { '已连接' } else { '未连接' }))
    foreach ($t in (Get-Texts -Window $w)) {
        if ($t -match '正在接收|Receiving|Paired|Idle|正在播放') { Write-Host ("  文本: {0}" -f $t) }
    }
    if ($conn) { exit 0 } else { exit 1 }
}

# ---------------------------------------------------------------- 确保 App 在跑

$win = Get-AppWindow
if (-not $win) {
    if ($NoLaunch) { Write-Step 'App 未运行，且指定了 -NoLaunch'; exit 2 }
    if (-not (Test-Path $ExePath)) { Write-Step ("找不到可执行文件: {0}" -f $ExePath); exit 2 }
    Write-Step ("启动 {0}" -f (Split-Path $ExePath -Leaf))
    Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path $ExePath -Parent) | Out-Null
    for ($i = 0; $i -lt 25; $i++) {
        Start-Sleep -Milliseconds 700
        $win = Get-AppWindow
        if ($win) { break }
    }
    if (-not $win) { Write-Step 'App 启动超时'; exit 2 }
}
Write-Step 'App 已就绪'

# 自编版一般会自动连上，先给它几秒
for ($i = 0; $i -lt 8; $i++) {
    if (Test-Connected -Window $win) {
        Write-Step '已自动连接（自编版会自动选中设备）'
        exit 0
    }
    Start-Sleep -Milliseconds 700
}

# ---------------------------------------------------------------- 选中设备 → 点连接

$itemCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
if ($items.Count -eq 0) {
    $refresh = Get-ButtonByName -Window $win -Names @('Geräte aktualisieren', 'Refresh', '刷新')
    if ($refresh) {
        Write-Step '列表为空，点一次刷新'
        $refresh.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 3
        $items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
    }
}
if ($items.Count -eq 0) { Write-Step '设备列表为空（手机可能不在范围内）'; exit 3 }

function Get-ItemName {
    param($Item)
    # 自编版 ListItem 的 Name 是模型类名，设备名在子 Text 里
    $n = $Item.Current.Name
    if ($n -and $n -notmatch '^BluetoothAudioReceiver\.') { return $n }
    $tCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $t = $Item.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $tCond)
    if ($t) { return $t.Current.Name }
    return $n
}

$target = $items[0]
foreach ($it in $items) {
    if ((Get-ItemName -Item $it) -match [regex]::Escape($DeviceName)) { $target = $it; break }
}
Write-Step ("选中设备: {0}" -f (Get-ItemName -Item $target))
try { $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
catch { Write-Step '选中失败（可能已选中）' }
Start-Sleep -Milliseconds 600

$btn = Get-ButtonByName -Window $win -Names $ConnectNames
if ($btn -and $btn.Current.IsEnabled) {
    Write-Step ("点击 {0}" -f $btn.Current.Name)
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
elseif ($btn) {
    Write-Step ("{0} 当前不可用，可能正在连接" -f $btn.Current.Name)
}
else { Write-Step '找不到连接按钮'; exit 4 }

# ---------------------------------------------------------------- 校验

for ($i = 0; $i -lt $TimeoutSeconds; $i++) {
    Start-Sleep -Seconds 1
    if (Test-Connected -Window $win) { Write-Step '连接成功'; exit 0 }
}
Write-Step '超时仍未连上'
exit 5
