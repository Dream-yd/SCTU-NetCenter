#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$StatePath = 'E:\MobileHotspotKeeper\state.json',
    [string]$KeeperPath = 'E:\MobileHotspotKeeper\Keep-HotspotOn.ps1'
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { throw '找不到热点状态文件，未修改。' }
if (-not (Test-Path -LiteralPath $KeeperPath -PathType Leaf)) { throw '找不到热点策略脚本，未修改。' }
$state = Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($name in @('Boot','Manual','LastLink','LastOn')) {
    if ($null -eq $state -or $null -eq $state.PSObject.Properties[$name]) { throw '热点状态文件格式不完整，未修改。' }
}
$state.Manual = ''
# Clearing the last observation avoids detecting the same state as a new manual action.
$state.LastOn = $null
if ($state.PSObject.Properties['Updated']) { $state.Updated = (Get-Date -Format 'o') }
else { $state | Add-Member -NotePropertyName Updated -NotePropertyValue (Get-Date -Format 'o') }
$folder = Split-Path -Parent $StatePath
$temporary = Join-Path $folder ('.netcenter-clear-' + [guid]::NewGuid().ToString('N') + '.tmp')
$backup = $StatePath + '.before-clear-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N') + '.bak'
try {
    [IO.File]::WriteAllText($temporary, ($state | ConvertTo-Json -Depth 20 -Compress), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::Replace($temporary, $StatePath, $backup)
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
$global:LASTEXITCODE = 0
& $KeeperPath
if ($LASTEXITCODE -ne 0) { throw "已清除覆盖，但策略脚本失败（退出码 $LASTEXITCODE）。" }
Write-Output '已清除热点手动覆盖并执行策略。'
