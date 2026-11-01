$ErrorActionPreference='Stop'
$global:NetCenterFakeTray=[pscustomobject]@{TaskName='NetCenter';Settings=[pscustomobject]@{Enabled=$true};Actions=@([pscustomobject]@{Execute='wscript.exe';Arguments='"E:\NetCenter\Start-NetCenter.vbs"';WorkingDirectory=''})}
function Get-ScheduledTask {param([string]$TaskName) return $global:NetCenterFakeTray}
function New-ScheduledTaskAction {param([string]$Execute,[string]$Argument,[string]$WorkingDirectory) return [pscustomobject]@{Execute=$Execute;Arguments=$Argument;WorkingDirectory=$WorkingDirectory}}
function Set-ScheduledTask {param([string]$TaskName,$Action) $global:NetCenterFakeTray.Actions=@($Action); return $global:NetCenterFakeTray}
$base=Join-Path $env:TEMP ('netcenter-tray-test-'+[guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $base | Out-Null
$installer=Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\Install-Tray.ps1'
$release=[IO.File]::ReadAllText((Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\current-release.txt'))
$exe=Join-Path $release 'NetCenter.exe'
& $installer -Apply -AppPath $exe -DataRoot $base | Out-Null
if($global:NetCenterFakeTray.Actions[0].Execute -notmatch 'wscript' -or $global:NetCenterFakeTray.Actions[0].Arguments -notmatch 'Start-NetCenter\.vbs'){throw 'Tray task not switched to detached launcher'}
$backup=(Get-ChildItem -LiteralPath $base -Directory | Select-Object -First 1).FullName
& $installer -Apply -AppPath $exe -DataRoot $base | Out-Null
& $installer -Restore $backup -DataRoot $base | Out-Null
if($global:NetCenterFakeTray.Actions[0].Execute -ne 'wscript.exe'){throw 'Tray task not restored'}
