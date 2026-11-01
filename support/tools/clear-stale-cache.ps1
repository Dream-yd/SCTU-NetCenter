#Requires -Version 5.1
<#
.SYNOPSIS
    清理校园网认证的「过期缓存」（query_cache.txt 里记的是别的网卡的 IP）。

.DESCRIPTION
    背景：`E:\CampusAuth\query_cache.txt` 保存的是**上一次登录成功**时的完整
    queryString，里面有 wlanuserip / wlanparameter / userlocation。

    换链路（有线 ↔ WiFi）后，DHCP 会给你一个新 IP，但缓存里还写着旧 IP。
    campus_auth 自己会校验、不拿它去登录；**但外部消费者（比如 NetCenter 的
    「校园认证」卡片）会把它当成"当前会话"显示** —— 于是面板上出现一个
    根本不属于当前链路的 IP，看着像故障。

    这个脚本把这种缓存挪进 `_backup\query_cache.txt.stale-<时间戳>`。
    用「挪」不用「删」：里面是当时完整的登录参数，出问题还能翻出来看
    userlocation / wlanparameter 到底是多少。

.PARAMETER Quiet
    不输出任何东西（给程序调用）。
.PARAMETER Force
    即使当前没有任何校园网 IP 也执行（默认更保守：链路没起来时不动）。
.PARAMETER WhatIfRun
    只报告会做什么，不真的动文件。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File "E:\NetCenter\tools\clear-stale-cache.ps1"
    powershell -ExecutionPolicy Bypass -File "E:\NetCenter\tools\clear-stale-cache.ps1" -Quiet

.NOTES
    退出码：0 = 没有过期缓存 / 未处理；10 = 已隔离；2 = 参数缺失等错误。
#>
[CmdletBinding()]
param(
    [switch]$Quiet,
    [switch]$Force,
    [switch]$WhatIfRun,
    [string]$CacheFile = 'E:\CampusAuth\query_cache.txt',
    [string]$BackupDir = 'E:\CampusAuth\_backup'
)

$ErrorActionPreference = 'Stop'

function Say { param([string]$Msg) if (-not $Quiet) { Write-Host $Msg } }

if (-not (Test-Path $CacheFile)) {
    Say '没有 query_cache.txt，无需清理'
    exit 0
}

# ---- 1) 取缓存里的 IP
$raw = Get-Content $CacheFile -Raw -Encoding UTF8
if ($raw -notmatch 'wlanuserip=([0-9.]+)') {
    Say '缓存里没有 wlanuserip，跳过'
    exit 0
}
$cacheIp = $Matches[1]

# ---- 2) 取本机当前 IPv4（排除回环/链路本地/虚拟网卡）
$local = @()
foreach ($ni in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
    if ($ni.OperationalStatus -ne 'Up') { continue }
    $t = $ni.NetworkInterfaceType
    if ($t -ne 'Ethernet' -and $t -ne 'Wireless80211') { continue }
    if ([string]$ni.Description -match 'Virtual|Hyper-V|VMware|TAP|Loopback|Sangfor|Bluetooth|Mihomo|Meta|Tunnel|VPN') { continue }
    foreach ($u in $ni.GetIPProperties().UnicastAddresses) {
        if ($u.Address.AddressFamily -ne 'InterNetwork') { continue }
        $ip = $u.Address.IPAddressToString
        if ($ip -like '169.254.*' -or $ip -eq '127.0.0.1') { continue }
        $local += $ip
    }
}

if ($local.Count -eq 0 -and -not $Force) {
    Say '当前没有可用的物理网卡地址，保守起见不动缓存（要强制加 -Force）'
    exit 0
}

Say ("缓存里的 IP : {0}" -f $cacheIp)
Say ("本机当前 IP : {0}" -f (($local | Sort-Object) -join ', '))

if ($local -contains $cacheIp) {
    Say '缓存属于当前链路，无需清理'
    exit 0
}

# ---- 3) 隔离
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dest = Join-Path $BackupDir ("query_cache.txt.stale-{0}" -f $stamp)

if ($WhatIfRun) {
    Say ("[WhatIf] 会把 {0} 挪到 {1}" -f $CacheFile, $dest)
    exit 10
}

try {
    if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }
    Move-Item -LiteralPath $CacheFile -Destination $dest -Force
    Say ("已隔离过期缓存 → {0}" -f $dest)
    exit 10
}
catch {
    if (-not $Quiet) { Write-Warning ("隔离失败: {0}" -f $_.Exception.Message) }
    exit 2
}
