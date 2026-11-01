#Requires -Version 5.1
<#
.SYNOPSIS
    NetCenter —— 把校园网认证 / 移动热点 / Clash 状态整合成一个托盘小工具。

.DESCRIPTION
    右下角托盘图标，左键点开一个小弹窗，实时显示：
      · 当前链路（有线 / WiFi / 双链路）+ 本机 IP + 默认路由
      · 校园门户授权状态（已放行 / 未授权被拦 / 门户不可达）+ 会话 IP 与网卡 MAC
      · 移动热点（期望 / 实际 / 手动覆盖 / 客户端数 / SSID）
      · Clash（内核是否在跑 / TUN 开关）

    图标颜色就是整体健康度：绿=正常，黄=注意，红=异常，灰=无网络。

    状态全部在本进程内直接采集（CIM / WinRT / HTTP 探针），不依赖那两个脚本常驻；
    只有点按钮时才会去调用它们执行动作。

.PARAMETER Dump
    只采集并打印一次状态（纯文本，不建窗口），用来排查数据源。
.PARAMETER Force
    允许重复启动（默认单实例）。
#>
[CmdletBinding()]
param([switch]$Once, [int]$ParentPid = 0, [long]$ParentStartTicks = 0)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    throw '必须用 Windows PowerShell 5.1 运行（WinRT 投影在 PowerShell 7 里没有）。'
}

$Root = Split-Path -Parent $PSCommandPath
$LogDir = Join-Path $Root 'logs'
$script:Trace = [bool]$Trace
if ($script:Trace -and -not (Test-Path $LogDir)) {
    try { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null } catch { }
}

function Write-ErrLog {
    param([string]$Msg)
    try {
        if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
        Add-Content -Path (Join-Path $LogDir 'netcenter-error.log') -Encoding UTF8 `
            -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Msg)
    }
    catch { }
}

# ------------------------------------------------------------------ 配置

$DefaultCfg = [ordered]@{
    campusDir      = 'E:\CampusAuth'
    hotspotDir     = 'E:\MobileHotspotKeeper'
    refreshSeconds = 1     # UI 心跳：每秒重绘一次（只读缓存，约 2ms）
    linkSeconds    = 2     # 网卡快照间隔：纯 .NET 枚举，约 40ms
    probeSeconds   = 10    # 门户探测间隔：TCP+HTTP，约 60ms；状态一变会立刻提前探
    campusPrefixes = @('172.30.')
    portal         = '172.16.10.76'
    probeUrl       = 'http://223.5.5.5/'
    gptUrl         = 'https://chatgpt.com/'
    gptSeconds     = 15    # ChatGPT 探测间隔（后台异步，不阻塞 UI）
    directIpUrl    = 'https://myip.ipip.net'          # 走直连规则，回显本机公网出口
    proxyIpUrl     = 'https://ipinfo.io/json'         # 走代理规则，回显代理出口
    egressSeconds  = 30    # 出口 IP 探测间隔（变一次要好几秒，没必要太勤）
}
$Cfg = [ordered]@{}
foreach ($k in $DefaultCfg.Keys) { $Cfg[$k] = $DefaultCfg[$k] }
$cfgPath = Join-Path $Root 'config.json'
if (Test-Path $cfgPath) {
    try {
        $j = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($p in $j.PSObject.Properties) {
            if ($Cfg.Contains($p.Name)) { $Cfg[$p.Name] = $p.Value }
        }
    }
    catch { }
}
$AssetsDir = Join-Path $Root 'assets'

# ------------------------------------------------------------------ 采集

function Get-Ipv4 {
    param([int]$Index)
    $a = Get-NetIPAddress -InterfaceIndex $Index -AddressFamily IPv4 -ErrorAction SilentlyContinue |
         Where-Object { $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1
    if ($a) { return $a.IPAddress }
    return ''
}

function Test-Tcp {
    param([string]$Target, [int]$Port, [int]$TimeoutMs = 500)
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $ok = $c.ConnectAsync($Target, $Port).Wait($TimeoutMs)
        return $ok
    }
    catch { return $false }
    finally { $c.Close() }
}

# ---------------------------------------------------------------- 延迟与速率

# 带计时的 TCP 连接（门户延迟用）。Ok=能否连上，Ms=耗时
function Test-TcpTimed {
    param([string]$Target, [int]$Port, [int]$TimeoutMs = 600)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $ok = $c.ConnectAsync($Target, $Port).Wait($TimeoutMs)
        $sw.Stop()
        if ($ok) { return [pscustomobject]@{ Ok = $true; Ms = [int]$sw.Elapsed.TotalMilliseconds } }
        return [pscustomobject]@{ Ok = $false; Ms = $TimeoutMs }
    }
    catch { return [pscustomobject]@{ Ok = $false; Ms = $TimeoutMs } }
    finally { $c.Close() }
}

# 字节/秒 → 自适应单位
function Format-Rate {
    param([double]$BytesPerSec = -1)
    if ($BytesPerSec -lt 0) { return '--' }
    if ($BytesPerSec -lt 1024) { return ('{0:N0} B/s' -f $BytesPerSec) }
    if ($BytesPerSec -lt 1048576) { return ('{0:N0} KB/s' -f ($BytesPerSec / 1024)) }
    if ($BytesPerSec -lt 1073741824) { return ('{0:N1} MB/s' -f ($BytesPerSec / 1048576)) }
    return ('{0:N2} GB/s' -f ($BytesPerSec / 1073741824))
}

# 实时网速：累计字节 → 每秒速率（用上一次采样做差分）
function Get-SpeedSample {
    $nics = @($script:SpeedNics | Where-Object { $_ })
    if ($nics.Count -eq 0) {
        return [pscustomobject]@{ RxText = '--'; TxText = '--'; Rx = -1; Tx = -1 }
    }
    $rx = [double]0; $tx = [double]0
    foreach ($ni in $nics) {
        try {
            $st = $ni.GetIPv4Statistics()
            $rx += $st.BytesReceived
            $tx += $st.BytesSent
        }
        catch { }
    }
    $now = Get-Date
    $dRx = -1; $dTx = -1
    if ($script:SpeedPrev -and $script:SpeedPrevAt) {
        $secs = ($now - $script:SpeedPrevAt).TotalSeconds
        if ($secs -gt 0.2) {
            $dRx = [Math]::Max(0, ($rx - $script:SpeedPrev.Rx) / $secs)
            $dTx = [Math]::Max(0, ($tx - $script:SpeedPrev.Tx) / $secs)
        }
    }
    $script:SpeedPrev = @{ Rx = $rx; Tx = $tx }
    $script:SpeedPrevAt = $now
    return [pscustomobject]@{
        RxText = Format-Rate $dRx
        TxText = Format-Rate $dTx
        Rx = $dRx
        Tx = $dTx
    }
}

# ---------------------------------------------------------------- ChatGPT 探测（异步）
#
# 同步探测最坏要等 3 秒，会把 UI 卡住。这里用独立 runspace 在后台跑，
# 每秒 poll 一次结果，主线程永远不阻塞。

function Start-GptProbe {
    if ($script:GptPs) { return }
    $ps = [powershell]::Create()
    $null = $ps.AddScript({
            param($url, $timeoutMs)
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            function Done($ok, $code) {
                $sw.Stop()
                [pscustomobject]@{ Ok = $ok; Code = $code; Ms = [int]$sw.Elapsed.TotalMilliseconds }
            }
            try {
                $req = [System.Net.HttpWebRequest]::Create($url)
                $req.Method = 'HEAD'
                $req.AllowAutoRedirect = $false
                $req.Timeout = $timeoutMs
                $req.ReadWriteTimeout = $timeoutMs
                $req.Proxy = $null
                $req.UserAgent = 'NetCenter/1.0'
                $resp = $req.GetResponse()
                $code = [int]$resp.StatusCode
                $resp.Close()
                return (Done $true $code)
            }
            catch [System.Net.WebException] {
                $r = $_.Exception.Response
                if ($r) {
                    $code = [int]$r.StatusCode
                    $r.Close()
                    # 401/403/503 = 被 Cloudflare 挑战，但链路是通的
                    return (Done $true $code)
                }
                return (Done $false 0)
            }
            catch { return (Done $false 0) }
        }).AddArgument($Cfg.gptUrl).AddArgument(3000)
    $script:GptPs = $ps
    $script:GptAsync = $ps.BeginInvoke()
}

function Update-GptProbe {
    if (-not $script:GptPs -or -not $script:GptAsync) { return }
    if (-not $script:GptAsync.IsCompleted) { return }
    try {
        $out = $script:GptPs.EndInvoke($script:GptAsync)
        if ($out -and $out.Count -gt 0 -and $out[0]) {
            $r = $out[0]
            $script:GptResult = [pscustomobject]@{
                Ok = [bool]$r.Ok; Code = [int]$r.Code; Ms = [int]$r.Ms; At = Get-Date
            }
        }
    }
    catch { }
    finally {
        try { $script:GptPs.Dispose() } catch { }
        $script:GptPs = $null
        $script:GptAsync = $null
    }
}

# ---------------------------------------------------------------- 出口 IP 探测（异步）
#
# 直连出口：myip.ipip.net —— 在国内规则里走 DIRECT，回显的是本机真实公网出口。
# 代理出口：ipinfo.io     —— 走代理规则，回显的是节点出口。
# 两个都放在后台 runspace，主线程只 poll。

function Start-EgressProbe {
    if ($script:EgPs) { return }
    $ps = [powershell]::Create()
    $null = $ps.AddScript({
            param($directUrl, $proxyUrl, $timeoutMs)
            function GetBody($u) {
                try {
                    $req = [System.Net.HttpWebRequest]::Create($u)
                    $req.Timeout = $timeoutMs
                    $req.ReadWriteTimeout = $timeoutMs
                    $req.Proxy = $null
                    $req.UserAgent = 'NetCenter/1.0'
                    $resp = $req.GetResponse()
                    $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
                    $b = $sr.ReadToEnd()
                    $sr.Close(); $resp.Close()
                    return $b
                }
                catch { return '' }
            }
            $d = ''; $geo = ''
            $bd = GetBody $directUrl
            if ($bd -match '([0-9]{1,3}(?:\.[0-9]{1,3}){3})') { $d = $Matches[1] }
            if ($bd -match '来自于[:：]\s*(\S.*)$') { $geo = $Matches[1].Trim() }

            $p = ''; $pg = ''
            $bp = GetBody $proxyUrl
            if ($bp -match '"ip"\s*:\s*"([^"]+)"') { $p = $Matches[1] }
            if ($bp -match '"country"\s*:\s*"([^"]+)"') { $pg = $Matches[1] }
            $city = ''
            if ($bp -match '"city"\s*:\s*"([^"]+)"') { $city = $Matches[1] }
            if ($city) { $pg = "$pg $city" }

            [pscustomobject]@{ Direct = $d; DirectGeo = $geo; Proxy = $p; ProxyGeo = $pg.Trim() }
        }).AddArgument($Cfg.directIpUrl).AddArgument($Cfg.proxyIpUrl).AddArgument(3500)
    $script:EgPs = $ps
    $script:EgAsync = $ps.BeginInvoke()
}

function Update-EgressProbe {
    if (-not $script:EgPs -or -not $script:EgAsync) { return }
    if (-not $script:EgAsync.IsCompleted) { return }
    try {
        $out = $script:EgPs.EndInvoke($script:EgAsync)
        if ($out -and $out.Count -gt 0 -and $out[0]) {
            $r = $out[0]
            if ($r.Direct -or $r.Proxy) {
                $script:EgResult = [pscustomobject]@{
                    Direct = [string]$r.Direct; DirectGeo = [string]$r.DirectGeo
                    Proxy  = [string]$r.Proxy;  ProxyGeo  = [string]$r.ProxyGeo
                    At     = Get-Date
                }
            }
        }
    }
    catch { }
    finally {
        try { $script:EgPs.Dispose() } catch { }
        $script:EgPs = $null
        $script:EgAsync = $null
    }
}

# 对纯 IP 发一个不跟随跳转的请求：被 302 到门户 = 当前网卡未授权；正常响应 = 已放行
function Get-Probe {
    try {
        $req = [System.Net.HttpWebRequest]::Create($Cfg.probeUrl)
        $req.AllowAutoRedirect = $false
        $req.Timeout = 1200
        $req.ReadWriteTimeout = 1200
        $req.Proxy = $null
        $req.UserAgent = 'NetCenter/1.0'
        $resp = $req.GetResponse()
        $code = [int]$resp.StatusCode
        $loc = [string]$resp.Headers['Location']
        $resp.Close()
        return [pscustomobject]@{ Code = $code; Location = $loc; Reachable = $true }
    }
    catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($r) {
            $code = [int]$r.StatusCode
            $loc = [string]$r.Headers['Location']
            $r.Close()
            return [pscustomobject]@{ Code = $code; Location = $loc; Reachable = $true }
        }
        return [pscustomobject]@{ Code = 0; Location = ''; Reachable = $false }
    }
    catch {
        return [pscustomobject]@{ Code = 0; Location = ''; Reachable = $false }
    }
}

function Get-LinkState {
    # 纯 .NET（IP Helper API）枚举网卡：约 40ms，比 Get-NetAdapter + Get-NetIPAddress
    # + Get-NetRoute 那套 CIM 查询（约 180ms）快 4 倍多。
    $r = [pscustomobject]@{
        EthUp = $false; EthIp = ''; EthName = ''
        WifiUp = $false; WifiIp = ''; WifiName = ''; Ssid = ''
        DefaultRoute = ''; CampusIps = @(); Summary = ''; Sig = ''
    }
    $ethGw = ''; $wifiGw = ''
    $speedNics = @()

    foreach ($ni in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($ni.OperationalStatus -ne 'Up') { continue }
        $type = $ni.NetworkInterfaceType
        if ($type -ne 'Ethernet' -and $type -ne 'Wireless80211') { continue }

        # 排掉虚拟网卡（Hyper-V / VMware / TAP / VPN / 蓝牙 / TUN）
        $desc = [string]$ni.Description
        if ($desc -match 'Virtual|Hyper-V|VMware|TAP|Loopback|Sangfor|Bluetooth|Mihomo|Meta|Tunnel|VPN') { continue }

        $props = $ni.GetIPProperties()
        $ip = ''
        foreach ($u in $props.UnicastAddresses) {
            if ($u.Address.AddressFamily -eq 'InterNetwork' -and $u.Address.IPAddressToString -notlike '169.254.*') {
                $ip = $u.Address.IPAddressToString; break
            }
        }
        $gw = ''
        foreach ($g in $props.GatewayAddresses) { $gw = $g.Address.IPAddressToString; break }

        if ($type -eq 'Ethernet') {
            $r.EthUp = $true; $r.EthIp = $ip; $r.EthName = $ni.Name; $ethGw = $gw
        }
        else {
            $r.WifiUp = $true; $r.WifiIp = $ip; $r.WifiName = $ni.Name; $wifiGw = $gw
        }
        if ($ip) { $speedNics += $ni }
    }

    # 默认路由：有线优先（本机 metric 有线 25 < 无线 55）
    if ($r.EthUp -and $ethGw) { $r.DefaultRoute = "$($r.EthName) → $ethGw" }
    elseif ($r.WifiUp -and $wifiGw) { $r.DefaultRoute = "$($r.WifiName) → $wifiGw" }

    # SSID 要查 CIM（约 25ms），缓存 30 秒或换网时再查
    if ($r.WifiUp) {
        $stale = (-not $script:CachedSsid) -or
                 ($script:SsidFor -ne $r.WifiIp) -or
                 (-not $script:SsidAt) -or
                 (((Get-Date) - $script:SsidAt).TotalSeconds -gt 30)
        if ($stale) {
            try {
                $p = Get-NetConnectionProfile -ErrorAction Stop |
                     Where-Object { $_.InterfaceAlias -eq $r.WifiName } | Select-Object -First 1
                $script:CachedSsid = if ($p) { $p.Name } else { '' }
            }
            catch { $script:CachedSsid = '' }
            $script:SsidFor = $r.WifiIp
            $script:SsidAt = Get-Date
        }
        $r.Ssid = [string]$script:CachedSsid
    }

    $ips = @()
    if ($r.EthIp) { $ips += $r.EthIp }
    if ($r.WifiIp) { $ips += $r.WifiIp }
    $r.CampusIps = @($ips | Where-Object {
            $probeIp = $_
            ($Cfg.campusPrefixes | Where-Object { $probeIp.StartsWith($_) })
        })

    $script:SpeedNics = $speedNics     # 供 Get-SpeedSample 每秒读字节计数

    $parts = @()
    if ($r.EthUp) { $parts += "有线 $($r.EthIp)" }
    if ($r.WifiUp) { $parts += "WiFi $($r.Ssid) $($r.WifiIp)" }
    if ($parts.Count -eq 0) { $parts += '无链路' }
    $r.Summary = ($parts -join ' ＋ ')
    $r.Sig = "$($r.EthUp):$($r.EthIp)|$($r.WifiUp):$($r.WifiIp)|$($r.Ssid)"
    return $r
}

function Get-PortalState {
    param($Link)
    $onCampus = ($Link.CampusIps.Count -gt 0)
    $tcp = Test-TcpTimed -Target $Cfg.portal -Port 80 -TimeoutMs 350
    if (-not $tcp.Ok) {
        $sev = 'idle'
        if ($onCampus) { $sev = 'warn' }
        return [pscustomobject]@{
            Code = 'offcampus'; Text = '门户不可达'; Ms = -1
            Detail = '当前不在校园网（或链路未就绪）'; Severity = $sev
        }
    }
    $p = Get-Probe
    if (-not $p.Reachable) {
        return [pscustomobject]@{
            Code = 'blocked'; Text = '门户可达但外网不通'; Ms = $tcp.Ms
            Detail = "探测 $($Cfg.probeUrl) 无响应"; Severity = 'err'
        }
    }
    if ($p.Code -eq 302 -or $p.Location -match 'eportal|wlanuserip') {
        return [pscustomobject]@{
            Code = 'walled'; Text = '未授权（被拦到门户）'; Ms = $tcp.Ms
            Detail = '这块网卡的 IP 还没通过认证'; Severity = 'warn'
        }
    }
    return [pscustomobject]@{
        Code = 'authorized'; Text = '已放行'; Ms = $tcp.Ms
        Detail = "探测返回 HTTP $($p.Code)"; Severity = 'ok'
    }
}

function Get-CampusSession {
    $r = [pscustomobject]@{ State = ''; LastAction = ''; Account = ''; SessionIp = ''; Mac = '' }
    try {
        $cf = Join-Path $Cfg.campusDir 'config.json'
        if (Test-Path $cf) {
            $j = Get-Content $cf -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($j.user_id) { $r.Account = [string]$j.user_id }
        }
    }
    catch { }

    $stateFile = Join-Path $Cfg.campusDir 'last_state.txt'
    if (Test-Path $stateFile) {
        try { $r.State = (Get-Content $stateFile -Raw -Encoding UTF8).Trim() } catch { }
    }

    $logFile = Join-Path $Cfg.campusDir 'campus_auth.log'
    if (Test-Path $logFile) {
        try {
            $last = Get-Content $logFile -Tail 1 -Encoding UTF8
            if ($last -match '^\[([^\]]+)\]\s*(.+)$') {
                $ts = $Matches[1]
                if ($ts.Length -ge 19) { $ts = $ts.Substring(11) }
                $r.LastAction = "$ts $($Matches[2])"
            }
        }
        catch { }
    }

    $qs = Join-Path $Cfg.campusDir 'query_cache.txt'
    if (Test-Path $qs) {
        try {
            $t = Get-Content $qs -Raw -Encoding UTF8
            if ($t -match 'wlanuserip=([0-9.]+)') { $r.SessionIp = $Matches[1] }
            if ($t -match 'wlanparameter=([0-9a-fA-F-]{12,})') { $r.Mac = ($Matches[1] -replace '-', '').ToUpper() }
        }
        catch { }
    }
    return $r
}

# ---- WinRT（移动热点状态）
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime]
$null = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime]

function Get-HotspotState {
    $r = [pscustomobject]@{ State = 'Unknown'; Clients = 0; Ssid = ''; Manual = '' }
    try {
        $inet = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
        if ($inet) {
            $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($inet)
            $r.State = [string]$mgr.TetheringOperationalState
            $r.Clients = $mgr.ClientCount
            try { $r.Ssid = $mgr.GetCurrentAccessPointConfiguration().Ssid } catch { }
        }
        else { $r.State = 'NoInternet' }
    }
    catch { $r.State = 'Error' }

    $sj = Join-Path $Cfg.hotspotDir 'state.json'
    if (Test-Path $sj) {
        try {
            $s = Get-Content $sj -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($s.Manual) { $r.Manual = [string]$s.Manual }
        }
        catch { }
    }
    return $r
}

function Get-ClashState {
    $r = [pscustomobject]@{ Core = $false; Tun = $false }
    $r.Core = [bool](Get-Process verge-mihomo -ErrorAction SilentlyContinue)
    $vy = Join-Path $env:APPDATA 'io.github.clash-verge-rev.clash-verge-rev\verge.yaml'
    if (Test-Path $vy) {
        try {
            $line = Select-String -Path $vy -Pattern 'enable_tun_mode:\s*(true|false)' -Encoding UTF8 | Select-Object -First 1
            if ($line) { $r.Tun = ($line.Matches[0].Groups[1].Value -eq 'true') }
        }
        catch { }
    }
    return $r
}

function Get-QuickSig {
    # 极便宜的变化信号（约 9ms）：状态文件的 size+mtime + Clash 内核进程。
    #
    # PowerShell 托管的 WinForms 消息循环里，.NET / WMI 的后台线程事件回调
    # 不可靠（runspace 被 Application.Run 占着），所以真实事件监听在这里退化成
    # 「廉价指纹轮询」——每秒花不到 10ms，就能在 1 秒内感知到认证/热点/Clash 的任何变化。
    $parts = @()
    foreach ($f in @(
            (Join-Path $Cfg.campusDir 'query_cache.txt'),
            (Join-Path $Cfg.campusDir 'last_state.txt'),
            (Join-Path $Cfg.campusDir 'campus_auth.log'),
            (Join-Path $Cfg.hotspotDir 'state.json'))) {
        try {
            $fi = [System.IO.FileInfo]::new($f)
            if ($fi.Exists) { $parts += ('{0}:{1}' -f $fi.Length, $fi.LastWriteTimeUtc.Ticks) }
            else { $parts += '-' }
        }
        catch { $parts += '?' }
    }
    $parts += [string][bool](Get-Process verge-mihomo -ErrorAction SilentlyContinue)
    return ($parts -join '|')
}

function Get-AllState {
    param([switch]$ForceProbe)

    $now = Get-Date

    # 1) 网卡快照：每 linkSeconds 采一次（40ms/2s）
    $linkFresh = $script:CachedLink -and $script:LinkAt -and
                 ((($now - $script:LinkAt).TotalSeconds) -lt [double]$Cfg.linkSeconds)
    if ($linkFresh) {
        $link = $script:CachedLink
    }
    else {
        $link = Get-LinkState
        $script:CachedLink = $link
        $script:LinkAt = $now
    }

    # 2) 廉价指纹：每秒都算（9ms）
    $sig = Get-QuickSig

    # 3) 什么时候真的要探测：强制 / 指纹变 / 链路变 / 到点了
    $linkChanged = $script:LastLinkSig -and ($link.Sig -ne $script:LastLinkSig)
    $sigChanged = $script:LastSig -and ($sig -ne $script:LastSig)
    $probeDue = $ForceProbe -or
                (-not $script:CachedProbe) -or
                $linkChanged -or $sigChanged -or
                (-not $script:ProbeAt) -or
                ((($now - $script:ProbeAt).TotalSeconds) -ge [double]$Cfg.probeSeconds)

    if ($probeDue) {
        $script:CachedProbe = Get-PortalState -Link $link
        $script:CachedSess = Get-CampusSession
        $script:CachedHot = Get-HotspotState
        $script:CachedClash = Get-ClashState
        $script:ProbeAt = $now
        $script:LastSig = $sig
        $script:LastLinkSig = $link.Sig
    }

    $portal = $script:CachedProbe
    $sess = $script:CachedSess
    $hot = $script:CachedHot
    $clash = $script:CachedClash

    $ethUsable = ($link.EthUp -and $link.EthIp)
    $desired = ''
    if ($ethUsable) { $desired = 'On' }
    elseif ($link.WifiUp -and $link.WifiIp) { $desired = 'Off' }
    $hot | Add-Member -NotePropertyName Desired -NotePropertyValue $desired -Force

    $rank = @{ idle = 0; ok = 1; warn = 2; err = 3 }
    $sev = 0
    $consider = @($portal.Severity)
    if (-not $link.EthUp -and -not $link.WifiUp) { $consider += 'err' }
    if ($portal.Code -eq 'walled' -and $sess.State -eq 'action') { $consider += 'err' }
    if ($desired -and $hot.State -notin @('Unknown', 'NoInternet', 'Error') -and
        $hot.State -ne $desired -and -not $hot.Manual) { $consider += 'warn' }
    if (-not $clash.Core) { $consider += 'warn' }
    foreach ($s in $consider) {
        if ($rank.ContainsKey($s) -and $rank[$s] -gt $sev) { $sev = $rank[$s] }
    }
    $overall = 'idle'
    switch ($sev) { 1 { $overall = 'ok' } 2 { $overall = 'warn' } 3 { $overall = 'err' } }

    # 实时网速：每秒都算（不进缓存，才能看到瞬时速率）
    $speed = Get-SpeedSample

    # ChatGPT 探测：只在 TUN 开着时有意义。后台 runspace 跑，主线程不阻塞。
    Update-GptProbe
    if ($clash.Tun) {
        $gptDue = (-not $script:GptResult) -or
                  (((($now - $script:GptResult.At).TotalSeconds)) -ge [double]$Cfg.gptSeconds)
        if (($probeDue -or $gptDue) -and -not $script:GptPs) { Start-GptProbe }
    }
    $g = $script:GptResult
    if (-not $clash.Tun) { $gptText = 'TUN 未开' }
    elseif (-not $g) { $gptText = '检测中…' }
    elseif ($g.Ok) { $gptText = "$($g.Ms)ms" }
    else { $gptText = '不可达' }

    # 出口 IP（直连 / 代理），同样后台跑
    Update-EgressProbe
    $egDue = (-not $script:EgResult) -or
             (((($now - $script:EgResult.At).TotalSeconds)) -ge [double]$Cfg.egressSeconds)
    if ($egDue -and -not $script:EgPs) { Start-EgressProbe }
    $eg = $script:EgResult

    return [pscustomobject]@{
        When    = $now
        Link    = $link
        Portal  = $portal
        Session = $sess
        Hotspot = $hot
        Clash   = $clash
        Overall = $overall
        Speed   = $speed
        Gpt     = [pscustomobject]@{ Ok = [bool]($g -and $g.Ok); Ms = $(if ($g) { $g.Ms } else { -1 }); Text = $gptText }
        Egress  = [pscustomobject]@{
            Direct = $(if ($eg) { $eg.Direct } else { '' })
            DirectGeo = $(if ($eg) { $eg.DirectGeo } else { '' })
            Proxy  = $(if ($eg) { $eg.Proxy } else { '' })
            ProxyGeo  = $(if ($eg) { $eg.ProxyGeo } else { '' })
        }
    }
}


[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$ProgressPreference = 'SilentlyContinue'
while ($true) {
    if ($ParentPid -gt 0) {
        try {
            $owner = [System.Diagnostics.Process]::GetProcessById($ParentPid)
            $alive = -not $owner.HasExited -and $owner.StartTime.ToUniversalTime().Ticks -eq $ParentStartTicks
            $owner.Dispose()
            if (-not $alive) { break }
        } catch { break }
    }
    $snapshot = Get-AllState
    $json = $snapshot | ConvertTo-Json -Depth 8 -Compress
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
    if ($Once) { break }
    Start-Sleep -Seconds 1
}