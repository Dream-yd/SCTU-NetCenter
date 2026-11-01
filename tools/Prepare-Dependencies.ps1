# 用于全新环境的辅助文件准备；不会覆盖已有目录。
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath 'E:\' -PathType Container)) { throw '此版本的固定路径依赖 E: 盘；请先阅读 README 并按实际环境调整源码。' }
$destinations = @('E:\CampusAuth','E:\MobileHotspotKeeper','E:\NetCenter\tools','E:\NetCenter\assets')
$existing = @($destinations | Where-Object { Test-Path -LiteralPath $_ })
if ($existing.Count -gt 0) { throw ('检测到已有目录，不会覆盖：' + ($existing -join ', ') + '。请参照 README 手动检查。') }
foreach ($path in $destinations) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
Copy-Item -LiteralPath (Join-Path $project 'support\campus-auth\campus_auth.py') -Destination 'E:\CampusAuth\campus_auth.py'
Copy-Item -LiteralPath (Join-Path $project 'support\campus-auth\config.example.json') -Destination 'E:\CampusAuth\config.json'
Copy-Item -LiteralPath (Join-Path $project 'support\hotspot\Keep-HotspotOn.ps1'),(Join-Path $project 'support\hotspot\Set-Hotspot.ps1') -Destination 'E:\MobileHotspotKeeper'
Copy-Item -LiteralPath (Join-Path $project 'support\hotspot\config.example.json') -Destination 'E:\MobileHotspotKeeper\config.json'
Copy-Item -LiteralPath (Join-Path $project 'support\tools\clear-stale-cache.ps1') -Destination 'E:\NetCenter\tools'
Get-ChildItem -LiteralPath (Join-Path $project 'assets') -Filter 'icon-tray-*.ico' -File | Copy-Item -Destination 'E:\NetCenter\assets'
Write-Output '辅助文件已准备。热点策略默认关闭；账号请在应用内添加。'
