#Requires -Version 5.1
<#
.SYNOPSIS
    移动热点按链路自动开关：插网线自动开、连 WiFi 自动关；手动操作优先到下次重启。

.DESCRIPTION
    由计划任务每 2 分钟跑一次（也可以手动运行）。每次运行做四件事：

      1. 判断当前链路：有线是否可用（连着 + 有 IP + 有默认路由）、WiFi 是否可用
      2. 按 config.json 算出「期望的热点状态」
      3. 检测「非本脚本发起」的状态变化 —— 那一定是人手动的，记成手动覆盖
      4. 只在没有手动覆盖时，把热点拨到期望状态

    手动覆盖（Manual）一旦记下就**保持到下次开机**：
      手动关掉  → 本次开机内不再自动开
      手动打开  → 本次开机内不再自动关
    重启后 state.json 里的 Boot 对不上，覆盖自动清空。

    怎么区分「手动」和「系统自己把它关了」：
      只有「链路特征没变（Signature 相同）+ 热点状态翻了」才判定为手动。
      链路一变（插拔网线、WiFi 重连），状态变化归因于链路，不算手动。

    必须用 Windows PowerShell 5.1 运行（powershell.exe）。
    PowerShell 7 没有 WinRT 投影，调不了这套 API。

.PARAMETER StatusOnly
    只打印当前链路/期望/实际/手动覆盖，不做任何修改，也不写日志。

.PARAMETER ConfigPath
    指定配置文件，默认 <脚本目录>\config.json。
#>
[CmdletBinding()]
param(
    [switch]$StatusOnly,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    throw '此脚本必须用 Windows PowerShell 5.1 运行（powershell.exe），PowerShell 7 不支持 WinRT。'
}

$Root      = Split-Path -Parent $PSCommandPath
$LogDir    = Join-Path $Root 'logs'
$LogFile   = Join-Path $LogDir 'keeper.log'
$StateFile = Join-Path $Root 'state.json'
$MaxLogBytes = 512KB

if (-not $ConfigPath) { $ConfigPath = Join-Path $Root 'config.json' }

# ---------------------------------------------------------------- 配置

$Defaults = [ordered]@{
    Enabled                   = $true     # 总开关；false = 脚本完全不动作
    OnEthernet                = 'on'      # 有线可用时：on 开热点 / off 关 / keep 不动
    OnWifi                    = 'off'     # 只有 WiFi 可用时：on / off / keep
    NoLink                    = 'keep'    # 两种链路都不可用时：keep 最安全
    ManualOverrideUntilReboot = $true     # 手动操作保持到下次重启
    RequireAcPower            = $false    # true = 电池供电时完全不动
    HeartbeatMinutes          = 30        # 无事可做时多久写一条心跳
}

function Get-Config {
    param([string]$Path)
    $cfg = [ordered]@{}
    foreach ($k in $Defaults.Keys) { $cfg[$k] = $Defaults[$k] }
    if (Test-Path -LiteralPath $Path) {
        try {
            $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
            $j = $raw | ConvertFrom-Json
            foreach ($p in $j.PSObject.Properties) {
                if ($cfg.Contains($p.Name)) { $cfg[$p.Name] = $p.Value }
            }
        }
        catch {
            if ($StatusOnly) { Write-Host "配置文件读取失败，用默认值：$($_.Exception.Message)" }
        }
    }
    return $cfg
}

$Cfg = Get-Config -Path $ConfigPath

# ---------------------------------------------------------------- 日志

function Write-Log {
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet('INFO', 'OK', 'WARN', 'ERROR')][string]$Level = 'INFO'
    )
    if ($StatusOnly) { return }
    $line = '{0} [{1,-5}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    try {
        if (-not (Test-Path -LiteralPath $LogDir)) {
            New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
        }
        $item = Get-Item -LiteralPath $LogFile -ErrorAction SilentlyContinue
        if ($item -and $item.Length -gt $MaxLogBytes) {
            Move-Item -LiteralPath $LogFile -Destination ($LogFile + '.1') -Force
        }
        Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8
    }
    catch { }   # 日志写不进去也不能让守护逻辑挂掉
}

function Get-LastLogAgeMinutes {
    try {
        $last = Get-Content -LiteralPath $LogFile -Tail 1 -ErrorAction Stop
        if ($last -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
            $t = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', [cultureinfo]::InvariantCulture)
            return ((Get-Date) - $t).TotalMinutes
        }
    }
    catch { }
    return [double]::MaxValue
}

# ---------------------------------------------------------------- 状态文件

function Get-BootId {
    try {
        return (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToString('o')
    }
    catch { return '' }
}

function Read-KeeperState {
    $s = [pscustomobject]@{
        Boot     = ''
        Manual   = ''      # '' | 'on' | 'off'
        LastLink = ''
        LastOn   = $null
    }
    if (Test-Path -LiteralPath $StateFile) {
        try {
            $j = Get-Content -LiteralPath $StateFile -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($j.Boot)     { $s.Boot = [string]$j.Boot }
            if ($j.Manual)   { $s.Manual = [string]$j.Manual }
            if ($j.LastLink) { $s.LastLink = [string]$j.LastLink }
            if ($null -ne $j.LastOn) { $s.LastOn = [bool]$j.LastOn }
        }
        catch { }
    }
    return $s
}

function Save-KeeperState {
    param($State)
    $o = [ordered]@{
        Boot     = $State.Boot
        Manual   = $State.Manual
        LastLink = $State.LastLink
        LastOn   = $State.LastOn
        Updated  = (Get-Date -Format 'o')
    }
    try {
        $o | ConvertTo-Json -Compress | Set-Content -LiteralPath $StateFile -Encoding UTF8
    }
    catch { }
}

# ---------------------------------------------------------------- 链路

function Get-LinkState {
    $r = [pscustomobject]@{
        EthLink = $false; EthUsable = $false; EthIndex = 0; EthIp = ''; EthName = ''
        WifiLink = $false; WifiUsable = $false; WifiIndex = 0; WifiIp = ''; WifiName = ''
        Signature = ''; Text = ''
    }

    $phys = Get-NetAdapter -Physical -ErrorAction SilentlyContinue
    foreach ($a in $phys) {
        $isEth  = ($a.PhysicalMediaType -eq '802.3')
        $isWifi = ($a.PhysicalMediaType -eq 'Native 802.11')
        if (-not ($isEth -or $isWifi)) { continue }
        if ($a.Status -ne 'Up') { continue }

        $ip = Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
              Where-Object { $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1
        $hasGw = @(Get-NetRoute -InterfaceIndex $a.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue).Count -gt 0

        if ($isEth) {
            $r.EthLink  = $true
            $r.EthIndex = $a.ifIndex
            $r.EthName  = $a.Name
            if ($ip) { $r.EthIp = $ip.IPAddress }
            $r.EthUsable = ($ip -and $hasGw)
        }
        else {
            $r.WifiLink  = $true
            $r.WifiIndex = $a.ifIndex
            $r.WifiName  = $a.Name
            if ($ip) { $r.WifiIp = $ip.IPAddress }
            $r.WifiUsable = ($ip -and $hasGw)
        }
    }

    # 链路特征：只看「有没有链路 + 是哪块 + 拿的什么 IP」。
    # 用它来区分「状态变化是链路引起的」还是「人手动的」。
    $r.Signature = 'e{0}:{1}|w{2}:{3}' -f $r.EthIndex, $r.EthIp, $r.WifiIndex, $r.WifiIp

    $parts = @()
    if ($r.EthLink)  { $parts += ('有线 {0}({1})' -f $r.EthName, $(if ($r.EthIp) { $r.EthIp } else { '无IP' })) }
    if ($r.WifiLink) { $parts += ('WiFi {0}({1})' -f $r.WifiName, $(if ($r.WifiIp) { $r.WifiIp } else { '无IP' })) }
    if ($parts.Count -eq 0) { $parts += '无链路' }
    $r.Text = ($parts -join ' + ')
    return $r
}

# ---------------------------------------------------------------- WinRT

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime]
$null = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime]

# IAsyncOperation<T> → Task<T> 的反射桥（PowerShell 5.1 唯一的 await 办法）
$script:AsTaskMethod = [System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object {
        $_.Name -eq 'AsTask' -and
        $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1

function Wait-WinRtOperation {
    param(
        $Operation,
        [Type]$ResultType,
        [int]$TimeoutMs = 30000
    )
    $task = $script:AsTaskMethod.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    if (-not $task.Wait($TimeoutMs)) { return $null }
    return $task.Result
}

function Get-TetheringManager {
    $inet = $null
    try { $inet = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile() } catch { }
    if (-not $inet) { return $null }
    try {
        return [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($inet)
    }
    catch { return $null }
}

function Get-TetheringState {
    param($Manager)
    if (-not $Manager) { return 'Unknown' }
    return [string]$Manager.TetheringOperationalState
}

function Invoke-TetheringOperation {
    param(
        $Manager,
        [ValidateSet('Start', 'Stop')][string]$Action
    )
    $operation = if ($Action -eq 'Start') { $Manager.StartTetheringAsync() } else { $Manager.StopTetheringAsync() }
    $resultType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]
    $result = Wait-WinRtOperation -Operation $operation -ResultType $resultType -TimeoutMs 30000

    if (-not $result) {
        return [pscustomobject]@{ Ok = $false; Message = '等待系统响应超时（30 秒）' }
    }
    $status = [string]$result.Status
    if ($status -eq 'Success') {
        return [pscustomobject]@{ Ok = $true; Message = $status }
    }
    $extra = $result.AdditionalErrorMessage
    if ([string]::IsNullOrWhiteSpace($extra)) { $extra = '' } else { $extra = "（$extra）" }
    return [pscustomobject]@{ Ok = $false; Message = "$status$extra" }
}

# ---------------------------------------------------------------- 计算期望

function Resolve-Desired {
    param($Link, $Config)
    # 有线优先：两块同时可用时按「有线比无线稳」处理
    if ($Link.EthUsable) {
        switch ($Config.OnEthernet) {
            'on'  { return $true }
            'off' { return $false }
            default { return $null }
        }
    }
    if ($Link.WifiUsable) {
        switch ($Config.OnWifi) {
            'on'  { return $true }
            'off' { return $false }
            default { return $null }
        }
    }
    switch ($Config.NoLink) {
        'on'  { return $true }
        'off' { return $false }
        default { return $null }
    }
}

# ---------------------------------------------------------------- 主流程

$boot  = Get-BootId
$state = Read-KeeperState

# 换了一次开机 → 手动覆盖作废
if ($state.Boot -ne $boot) {
    if ($state.Boot) { Write-Log '检测到新的一次开机，清空手动覆盖。' }
    $state.Manual   = ''
    $state.LastLink = ''
    $state.LastOn   = $null
    $state.Boot     = $boot
}

$link = Get-LinkState

if ($StatusOnly) {
    $d = Resolve-Desired -Link $link -Config $Cfg
    $mgr = Get-TetheringManager
    Write-Host ('链路      : ' + $link.Text)
    Write-Host ('有线可用  : ' + $link.EthUsable + '    WiFi 可用 : ' + $link.WifiUsable)
    Write-Host ('期望热点  : ' + $(if ($null -eq $d) { 'keep（不动）' } elseif ($d) { '开' } else { '关' }))
    Write-Host ('实际热点  : ' + (Get-TetheringState -Manager $mgr))
    Write-Host ('手动覆盖  : ' + $(if ($state.Manual) { $state.Manual + '（到下次重启为止）' } else { '无' }))
    Write-Host ('链路特征  : ' + $link.Signature)
    Write-Host ('上次记录  : ' + $(if ($null -eq $state.LastOn) { '无' } elseif ($state.LastOn) { '开' } else { '关' }) + ' / ' + $state.LastLink)
    exit 0
}

if (-not $Cfg.Enabled) { exit 0 }

$onAc = $true
if ($Cfg.RequireAcPower) {
    Add-Type -AssemblyName System.Windows.Forms
    $onAc = ([System.Windows.Forms.SystemInformation]::PowerStatus.PowerLineStatus -eq
             [System.Windows.Forms.PowerLineStatus]::Online)
    if (-not $onAc) {
        if ((Get-LastLogAgeMinutes) -ge $Cfg.HeartbeatMinutes) {
            Write-Log '电池供电，按配置不干预。'
        }
        exit 0
    }
}

$mgr = Get-TetheringManager
if (-not $mgr) {
    # Wait-WinRtOperation 这条线上，管理器只能从「当前 Internet 连接」创建；
    # 没有 Internet 连接时查不到状态，也就没法安全地开/关。
    if ((Get-LastLogAgeMinutes) -ge $Cfg.HeartbeatMinutes) {
        Write-Log '当前没有可共享的 Internet 连接，跳过。' 'WARN'
    }
    exit 0
}

$cur = Get-TetheringState -Manager $mgr
$waited = 0
while ($cur -eq 'InTransition' -and $waited -lt 10) {
    Start-Sleep -Seconds 2
    $waited += 2
    $cur = Get-TetheringState -Manager $mgr
}

$desired  = Resolve-Desired -Link $link -Config $Cfg
$curOn    = ($cur -eq 'On')
$curKnown = ($cur -eq 'On' -or $cur -eq 'Off')

# ---- 手动覆盖检测
# 判据：链路特征没变 + 热点状态翻了 + 翻到的不是策略值 = 认定是手动操作。
#
# 已知局限（2026-10-27 实测到）：任何外部扰动（重启 Clash、驱动重载、网络栈重建）
# 也可能让热点自己掉，而链路特征不变 —— 会被误判成"手动关闭"。权衡后仍偏保守：
# 误判的代价是热点关着（点一下 NetCenter 的「清除手动覆盖」就恢复）；
# 另一边的代价是忽略用户的手动关闭、2 分钟后又给他开回来，明显更烦人。
if ($Cfg.ManualOverrideUntilReboot -and $curKnown -and $null -ne $desired) {
    $linkUnchanged = ($state.LastLink -eq $link.Signature)
    $stateFlipped  = ($null -ne $state.LastOn) -and ([bool]$state.LastOn -ne $curOn)

    if ($linkUnchanged -and $stateFlipped -and ($curOn -ne $desired)) {
        $state.Manual = if ($curOn) { 'on' } else { 'off' }
        $word = if ($curOn) { '开启' } else { '关闭' }
        Write-Log ("检测到手动{0}热点（链路未变）→ 本次开机内不再自动{1}。重启或点 NetCenter 的「清除手动覆盖」可恢复。" -f $word, $(if ($curOn) { '关闭' } else { '开启' })) 'WARN'
    }

    # 用户又把状态调回策略要求的那个值 → 覆盖作废，回到自动
    if ($state.Manual -and ($curOn -eq $desired)) {
        Write-Log ('热点状态已符合策略，撤销手动覆盖（原为 ' + $state.Manual + '）。')
        $state.Manual = ''
    }
}

# ---- 手动覆盖生效期：什么都不做
if ($state.Manual -and $null -ne $desired -and ($curOn -ne $desired)) {
    if ((Get-LastLogAgeMinutes) -ge $Cfg.HeartbeatMinutes) {
        Write-Log ('手动覆盖生效中（' + $state.Manual + '），保持现状。链路：' + $link.Text)
    }
    $state.LastLink = $link.Signature
    $state.LastOn   = $curOn
    Save-KeeperState -State $state
    exit 0
}

# ---- 没有可判断的期望：只更新记录
if ($null -eq $desired) {
    if ((Get-LastLogAgeMinutes) -ge $Cfg.HeartbeatMinutes) {
        Write-Log ('配置为 keep，保持现状（热点：' + $cur + '）。链路：' + $link.Text)
    }
    $state.LastLink = $link.Signature
    $state.LastOn   = $curOn
    Save-KeeperState -State $state
    exit 0
}

# ---- 已经符合期望
if ($curKnown -and $curOn -eq $desired) {
    if ((Get-LastLogAgeMinutes) -ge $Cfg.HeartbeatMinutes) {
        $clients = 0
        try { $clients = $mgr.ClientCount } catch { }
        Write-Log ('热点状态符合策略（' + $cur + '，客户端 ' + $clients + ' 台）。链路：' + $link.Text)
    }
    $state.LastLink = $link.Signature
    $state.LastOn   = $curOn
    Save-KeeperState -State $state
    exit 0
}

# ---- 需要动手
$action = if ($desired) { 'Start' } else { 'Stop' }
$label  = if ($desired) { '开启' } else { '关闭' }
$result = $null
for ($attempt = 1; $attempt -le 2; $attempt++) {
    $result = Invoke-TetheringOperation -Manager $mgr -Action $action
    if ($result.Ok) { break }
    if ($attempt -lt 2) { Start-Sleep -Seconds 4 }
}

if (-not $result.Ok) {
    Write-Log ("{0}移动热点失败：{1}（链路：{2}）" -f $label, $result.Message, $link.Text) 'ERROR'
    exit 2
}

Start-Sleep -Milliseconds 800
$final    = Get-TetheringState -Manager $mgr
$finalOn  = ($final -eq 'On')
$clients  = 0
$ssid     = ''
try {
    $clients = $mgr.ClientCount
    $ssid    = $mgr.GetCurrentAccessPointConfiguration().Ssid
} catch { }

Write-Log ("链路 {0} → 已{1}移动热点（SSID「{2}」，状态 {3}，客户端 {4} 台）。" -f
           $link.Text, $label, $ssid, $final, $clients) 'OK'

$state.LastLink = $link.Signature
$state.LastOn   = $finalOn
Save-KeeperState -State $state

if ($final -ne 'On' -and $final -ne 'Off') {
    Write-Log ("操作后状态仍为 {0}，等下一次自检。" -f $final) 'WARN'
    exit 2
}
if ($finalOn -ne $desired) {
    Write-Log ("操作后状态与期望不符（期望{0}，实际{1}）。" -f $label, $final) 'WARN'
    exit 2
}
exit 0
