$ErrorActionPreference='Stop'
$global:NetCenterFakeTasks=@{}
$global:NetCenterFail=$false
$global:NetCenterApplyCount=0
function Get-ScheduledTask { param([string]$TaskName) return $global:NetCenterFakeTasks[$TaskName] }
function New-ScheduledTaskAction { param([string]$Execute,[string]$Argument,[string]$WorkingDirectory) return [pscustomobject]@{Execute=$Execute;Arguments=$Argument;WorkingDirectory=$WorkingDirectory} }
function Set-ScheduledTask {
    param([string]$TaskName,$Action)
    if($global:NetCenterFail -and $Action.Arguments -match 'auto_auth\.py') { $global:NetCenterApplyCount++; if($global:NetCenterApplyCount -eq 2){throw 'simulated write failure'} }
    $global:NetCenterFakeTasks[$TaskName].Actions=@($Action)
    return $global:NetCenterFakeTasks[$TaskName]
}
$base=Join-Path $env:TEMP ('netcenter-migration-test-'+[guid]::NewGuid().ToString('N'))
$data=Join-Path $base 'data'; New-Item -ItemType Directory -Path $data -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $data 'default.json'),'{"name":"test"}')
[IO.File]::WriteAllText((Join-Path $data 'accounts.json'),'{"test":{"username":"u","password":"encrypted"}}')
$startup=Join-Path $base 'startup.vbs'; $original='old campus_auth.py startup'; [IO.File]::WriteAllText($startup,$original)
$installer=Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\Install-AutoAuth.ps1'
$python=Join-Path $env:LOCALAPPDATA 'Programs\Python\Python311\pythonw.exe'
function Reset-Fake {
 foreach($n in @('CampusNetAutoAuth_OnNetConnect','CampusNetAutoAuth_OnConnectivityChange')) {
   $global:NetCenterFakeTasks[$n]=[pscustomobject]@{TaskName=$n;Settings=[pscustomobject]@{Enabled=$true};Actions=@([pscustomobject]@{Execute='pythonw.exe';Arguments='campus_auth.py';WorkingDirectory=''})}
 }
 [IO.File]::WriteAllText($startup,$original)
}
Reset-Fake
& $installer -Apply -DataRoot $data -StartupPath $startup -PythonPath $python | Out-Null
foreach($t in $global:NetCenterFakeTasks.Values){if($t.Actions[0].Arguments -notmatch 'auto_auth\.py'){throw 'migration did not switch task'}}
$marker=Join-Path $base 'launcher-worked.txt'
$probe=Join-Path $base 'launcher_probe.py'
[IO.File]::WriteAllText($probe,('from pathlib import Path; Path(r"{0}").write_text("ok", encoding="utf-8")' -f $marker))
$launcher=[IO.File]::ReadAllText($startup)
$realRunner=Join-Path (Split-Path $PSScriptRoot -Parent) 'src\auto_auth.py'
$launcher=$launcher.Replace($realRunner,$probe)
$probeVbs=Join-Path $base 'launcher_probe.vbs'
[IO.File]::WriteAllText($probeVbs,$launcher,[Text.Encoding]::ASCII)
& cscript.exe //nologo $probeVbs
if($LASTEXITCODE -ne 0){throw 'generated VBS failed to parse or launch'}
for($i=0;$i -lt 60 -and -not (Test-Path -LiteralPath $marker);$i++){Start-Sleep -Milliseconds 100}
if(-not (Test-Path -LiteralPath $marker)){throw 'generated VBS did not start the runner'}
$backup=(Get-ChildItem -LiteralPath $data -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
& $installer -Restore $backup -DataRoot $data -StartupPath $startup -PythonPath $python | Out-Null
foreach($t in $global:NetCenterFakeTasks.Values){if($t.Actions[0].Arguments -ne 'campus_auth.py'){throw 'restore did not restore task'}}
if([IO.File]::ReadAllText($startup) -ne $original){throw 'startup not restored'}
Reset-Fake
$global:NetCenterFail=$true; $global:NetCenterApplyCount=0
try { & $installer -Apply -DataRoot $data -StartupPath $startup -PythonPath $python | Out-Null; throw 'expected failure' } catch { if($_.Exception.Message -eq 'expected failure'){throw} }
foreach($t in $global:NetCenterFakeTasks.Values){if($t.Actions[0].Arguments -ne 'campus_auth.py'){throw 'failed migration not rolled back'}}
if([IO.File]::ReadAllText($startup) -ne $original){throw 'startup not rolled back'}
