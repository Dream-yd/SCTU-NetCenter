#Requires -Version 5.1
<#
.SYNOPSIS
    手动开关 Windows 移动热点（排错、验证用）。

.DESCRIPTION
    守护脚本平时只负责「关了就打开」，不会主动关热点。
    这个脚本用来手工验证：先 -Action Off 关掉，等 2 分钟看守护是否自动开回来。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Set-Hotspot.ps1 -Action State

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Set-Hotspot.ps1 -Action Off

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Set-Hotspot.ps1 -Action On
#>
[CmdletBinding()]
param(
    [ValidateSet('State', 'On', 'Off')][string]$Action = 'State'
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    throw '必须用 Windows PowerShell 5.1（powershell.exe）运行，PowerShell 7 不支持 WinRT。'
}

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime]
$null = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime]

$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1

function Wait-WinRtOperation {
    param($Operation, [Type]$ResultType, [int]$TimeoutMs = 30000)
    $task = $asTask.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    if (-not $task.Wait($TimeoutMs)) { return $null }
    return $task.Result
}

$inet = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
if (-not $inet) { throw '当前没有可共享的 Internet 连接。' }

$mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($inet)
$resultType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]

switch ($Action) {
    'On' {
        $r = Wait-WinRtOperation -Operation $mgr.StartTetheringAsync() -ResultType $resultType
        "start -> $($r.Status)"
    }
    'Off' {
        $r = Wait-WinRtOperation -Operation $mgr.StopTetheringAsync() -ResultType $resultType
        "stop  -> $($r.Status)"
    }
}

$cfg = $mgr.GetCurrentAccessPointConfiguration()
"state -> $([string]$mgr.TetheringOperationalState)  (SSID: $($cfg.Ssid), clients: $($mgr.ClientCount)/$($mgr.MaxClientCount))"
