[CmdletBinding(DefaultParameterSetName='Inspect')]
param([Parameter(ParameterSetName='Inspect')][switch]$Inspect, [Parameter(ParameterSetName='Apply')][switch]$Apply, [Parameter(ParameterSetName='Restore')][string]$Restore, [string]$DataRoot, [string]$StartupPath, [string]$PythonPath)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$root = Split-Path -Parent $PSScriptRoot
$data = if ($DataRoot) { $DataRoot } else { Join-Path $env:USERPROFILE '.netcenter-next' }
$startup = if ($StartupPath) { $StartupPath } else { Join-Path ([Environment]::GetFolderPath('Startup')) 'CampusNetAutoAuth.vbs' }
$taskNames = @('CampusNetAutoAuth_OnNetConnect','CampusNetAutoAuth_OnConnectivityChange')
$tasks = @($taskNames | ForEach-Object { Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue })
if ($PSCmdlet.ParameterSetName -eq 'Inspect') {
    [pscustomobject]@{ Tasks = @($tasks | ForEach-Object { [pscustomobject]@{ Name=$_.TaskName; Enabled=$_.Settings.Enabled; Actions=@($_.Actions | ForEach-Object { [pscustomobject]@{Execute=$_.Execute; Arguments=$_.Arguments; WorkingDirectory=$_.WorkingDirectory} }) } }); StartupExists=(Test-Path -LiteralPath $startup); DefaultSelected=(Test-Path -LiteralPath (Join-Path $data 'default.json')) } | ConvertTo-Json -Depth 8 -Compress
    exit 0
}
function New-SavedActions($items) {
    @($items | ForEach-Object {
        $actionParameters=@{Execute=[string]$_.Execute}
        if ($_.Arguments) { $actionParameters.Argument=[string]$_.Arguments }
        if ($_.WorkingDirectory) { $actionParameters.WorkingDirectory=[string]$_.WorkingDirectory }
        New-ScheduledTaskAction @actionParameters
    })
}
if ($PSCmdlet.ParameterSetName -eq 'Restore') {
    $backup = (Resolve-Path -LiteralPath $Restore).Path
    $manifest = Get-Content -LiteralPath (Join-Path $backup 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($entry in $manifest.Tasks) {
        $current = Get-ScheduledTask -TaskName $entry.Name -ErrorAction Stop
        if (@($current.Actions | Where-Object { $_.Arguments -notmatch 'auto_auth\.py' }).Count -gt 0) { throw "任务 $($entry.Name) 已被其他程序更改，拒绝覆盖。" }
    }
    if (-not (Test-Path -LiteralPath $startup) -or -not ((Get-Content -LiteralPath $startup -Raw) -match 'NetCenterNext auto auth')) { throw '启动项已被其他程序更改，拒绝覆盖。' }
    foreach ($entry in $manifest.Tasks) { Set-ScheduledTask -TaskName $entry.Name -Action (New-SavedActions $entry.Actions) | Out-Null }
    Copy-Item -LiteralPath (Join-Path $backup 'startup.vbs') -Destination $startup -Force
    Write-Output '旧版自动认证入口已恢复。'
    exit 0
}
if ($tasks.Count -ne $taskNames.Count) { throw '未找到完整的原校园网事件任务；未进行修改。' }
$defaultPath=Join-Path $data 'default.json'; $accountPath=Join-Path $data 'accounts.json'
if (-not (Test-Path -LiteralPath $defaultPath) -or -not (Test-Path -LiteralPath $accountPath)) { throw '请先在新版中保存账号并设置自动认证账号。' }
$default = (Get-Content -LiteralPath $defaultPath -Raw -Encoding UTF8 | ConvertFrom-Json).name
$accounts = Get-Content -LiteralPath $accountPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $default -or -not $accounts.PSObject.Properties[$default]) { throw '自动认证账号无效，未进行修改。' }
$runner=Join-Path $root 'src\auto_auth.py'; $pythonw=if ($PythonPath) { $PythonPath } else { Join-Path $env:LOCALAPPDATA 'Programs\Python\Python311\pythonw.exe' }
if (-not (Test-Path -LiteralPath $runner) -or -not (Test-Path -LiteralPath $pythonw)) { throw '找不到自动认证入口或 pythonw。' }
if (-not (Test-Path -LiteralPath $startup) -or -not ((Get-Content -LiteralPath $startup -Raw) -match 'campus_auth\.py')) { throw '旧启动项不存在或不符合预期，未进行修改。' }
foreach($task in $tasks) { if (@($task.Actions).Count -ne 1 -or $task.Actions[0].Arguments -notmatch 'campus_auth\.py') { throw "任务 $($task.TaskName) 已更改，未进行修改。" } }
if (($runner.ToCharArray() | Where-Object {[int]$_ -gt 127}).Count -gt 0) { throw '项目路径包含非 ASCII 字符，无法安全生成 VBS 启动项。' }
$backup=Join-Path $data ('migration-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $backup | Out-Null
$manifest=[pscustomobject]@{ Tasks=@($tasks | ForEach-Object { [pscustomobject]@{Name=$_.TaskName;Actions=@($_.Actions | ForEach-Object {[pscustomobject]@{Execute=$_.Execute;Arguments=$_.Arguments;WorkingDirectory=$_.WorkingDirectory}})} }) }
[IO.File]::WriteAllText((Join-Path $backup 'manifest.json'),($manifest | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($true)))
Copy-Item -LiteralPath $startup -Destination (Join-Path $backup 'startup.vbs')
$action=New-ScheduledTaskAction -Execute $pythonw -Argument ('"{0}"' -f $runner) -WorkingDirectory (Split-Path $runner)
$launcher=@"
Option Explicit
' NetCenterNext auto auth
Dim shell, pythonw, runner
Set shell = CreateObject("WScript.Shell")
pythonw = shell.ExpandEnvironmentStrings("%LOCALAPPDATA%") & "\Programs\Python\Python311\pythonw.exe"
runner = "$runner"
shell.Run """" & pythonw & """ """ & runner & """", 0, False
"@
try {
    foreach($task in $tasks) { Set-ScheduledTask -TaskName $task.TaskName -Action $action | Out-Null }
    [IO.File]::WriteAllText($startup,$launcher,[Text.Encoding]::ASCII)
} catch {
    foreach($entry in $manifest.Tasks) { try { Set-ScheduledTask -TaskName $entry.Name -Action (New-SavedActions $entry.Actions) | Out-Null } catch { Write-Warning "恢复任务失败: $($entry.Name)" } }
    try { Copy-Item -LiteralPath (Join-Path $backup 'startup.vbs') -Destination $startup -Force } catch { Write-Warning '恢复启动项失败' }
    throw
}
Write-Output "已切换自动认证。恢复备份：$backup"
