$ErrorActionPreference='Stop'
$exe=$env:NETCENTER_TEST_EXE
if(-not $exe){throw 'NETCENTER_TEST_EXE must identify the release to test'}
$launcher=Join-Path (Split-Path $exe -Parent) 'Start-NetCenter.vbs'
if(-not (Test-Path -LiteralPath $launcher)){throw 'Launcher is missing'}
$env:NETCENTER_DATA_DIR=Join-Path $env:TEMP ('netcenter-launcher-'+[guid]::NewGuid().ToString('N'))
$env:NETCENTER_INSTANCE_KEY='launcher-'+[guid]::NewGuid().ToString('N')
$before=@(Get-CimInstance Win32_Process -Filter "Name='NetCenter.exe'" | Where-Object {$_.ExecutablePath -eq $exe} | ForEach-Object {$_.ProcessId})
$child=$null
try {
 & cscript.exe //nologo $launcher
 if($LASTEXITCODE -ne 0){throw 'Launcher failed'}
 for($i=0;$i -lt 60 -and -not $child;$i++){Start-Sleep -Milliseconds 100; $child=Get-CimInstance Win32_Process -Filter "Name='NetCenter.exe'" | Where-Object {$_.ExecutablePath -eq $exe -and $_.ProcessId -notin $before} | Select-Object -First 1}
 if(-not $child){throw 'Tray did not remain running after launcher exited'}
 Start-Sleep -Milliseconds 500
 if(-not (Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue)){throw 'Tray exited unexpectedly'}
} finally {
 if($child){Get-CimInstance Win32_Process | Where-Object {$_.ParentProcessId -eq $child.ProcessId -and $_.CommandLine -match 'collector.ps1'} | ForEach-Object {Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}; Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue}
}
