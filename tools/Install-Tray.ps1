[CmdletBinding(DefaultParameterSetName='Inspect')]
param([Parameter(ParameterSetName='Inspect')][switch]$Inspect, [Parameter(ParameterSetName='Apply')][switch]$Apply, [Parameter(ParameterSetName='Apply',Mandatory=$true)][string]$AppPath, [Parameter(ParameterSetName='Restore')][string]$Restore, [string]$DataRoot)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
$data=if($DataRoot){$DataRoot}else{Join-Path $env:USERPROFILE '.netcenter-next'}
$task=Get-ScheduledTask -TaskName 'NetCenter' -ErrorAction Stop
$actions=@($task.Actions)
if($actions.Count -ne 1){throw '启动任务包含多个操作，未进行修改。'}
if($PSCmdlet.ParameterSetName -eq 'Inspect'){
 [pscustomobject]@{Name=$task.TaskName;Enabled=$task.Settings.Enabled;Execute=$actions[0].Execute;Arguments=$actions[0].Arguments} | ConvertTo-Json -Compress
 exit 0
}
function Get-Action($item) {
 $parameters=@{Execute=[string]$item.Execute}
 if($item.Arguments){$parameters.Argument=[string]$item.Arguments}
 if($item.WorkingDirectory){$parameters.WorkingDirectory=[string]$item.WorkingDirectory}
 New-ScheduledTaskAction @parameters
}
if($PSCmdlet.ParameterSetName -eq 'Restore'){
 $manifest=Get-Content -LiteralPath (Join-Path (Resolve-Path -LiteralPath $Restore).Path 'tray-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
 if($actions[0].Execute -ne $manifest.NewExecute -or $actions[0].Arguments -ne $manifest.NewArguments){throw '启动任务已被其他程序修改，拒绝覆盖。'}
 Set-ScheduledTask -TaskName 'NetCenter' -Action (Get-Action $manifest.Original) | Out-Null
 Write-Output '旧托盘启动任务已恢复。'
 exit 0
}
$resolved=(Resolve-Path -LiteralPath $AppPath).Path
$release=Split-Path -Parent $resolved
if([IO.Path]::GetFileName($resolved) -ne 'NetCenter.exe' -or -not (Test-Path -LiteralPath (Join-Path $release 'backend\collector.ps1')) -or -not (Test-Path -LiteralPath (Join-Path $release 'backend\backend.py')) -or -not (Test-Path -LiteralPath (Join-Path $release 'Start-NetCenter.vbs'))){throw '发布目录不完整，未进行修改。'}
$oldLauncher = ($actions[0].Execute -match '(^|[\\/])wscript(\.exe)?$' -and $actions[0].Arguments -match 'E:\\NetCenter\\Start-NetCenter\.vbs')
$previousPreview = ($actions[0].Execute -match '^E:\\ChatGPT\\NetCenter\\artifacts\\release-[^\\]+\\NetCenter\.exe$' -and $actions[0].Arguments -eq '--background')
$currentReleaseLauncher = ($actions[0].Execute -match '(^|[\\/])wscript(\.exe)?$' -and $actions[0].Arguments -match '^"?E:\\ChatGPT\\NetCenter\\artifacts\\release-[^\\]+\\Start-NetCenter\.vbs"?$')
if(-not ($oldLauncher -or $previousPreview -or $currentReleaseLauncher)){throw '当前启动任务与旧版不一致，未进行修改。'}
$backup=Join-Path $data ('migration-tray-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$launcher=Join-Path $release 'Start-NetCenter.vbs'
$wscript=Join-Path $env:SystemRoot 'System32\wscript.exe'
$launcherArguments='"'+$launcher+'"'
$manifest=[pscustomobject]@{NewExecute=$wscript;NewArguments=$launcherArguments;Original=[pscustomobject]@{Execute=$actions[0].Execute;Arguments=$actions[0].Arguments;WorkingDirectory=$actions[0].WorkingDirectory}}
[IO.File]::WriteAllText((Join-Path $backup 'tray-manifest.json'),($manifest|ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($true)))
$new=New-ScheduledTaskAction -Execute $wscript -Argument $launcherArguments -WorkingDirectory $release
try {Set-ScheduledTask -TaskName 'NetCenter' -Action $new | Out-Null} catch {try {Set-ScheduledTask -TaskName 'NetCenter' -Action (Get-Action $manifest.Original) | Out-Null}catch{Write-Warning '恢复启动任务失败，请使用备份手动恢复。'}; throw}
Write-Output "托盘启动任务已切换，恢复备份：$backup"
