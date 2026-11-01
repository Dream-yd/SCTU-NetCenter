$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName PresentationFramework
if (-not [System.Windows.SystemParameters]::ClientAreaAnimation) { exit 0 }
$exe=if ($env:NETCENTER_TEST_EXE) { $env:NETCENTER_TEST_EXE } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'app\bin\Debug\net8.0-windows\NetCenter.exe' }
$env:NETCENTER_INSTANCE_KEY='motion-'+[guid]::NewGuid().ToString('N')
$env:NETCENTER_DATA_DIR=Join-Path $env:TEMP ('netcenter-motion-' + [guid]::NewGuid().ToString('N'))
$process=Start-Process -FilePath $exe -PassThru
try {
    $pidCondition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
    $nameCondition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'NetCenter 网络中心')
    $condition=New-Object System.Windows.Automation.AndCondition($pidCondition,$nameCondition)
    $win=$null
    for($i=0;$i -lt 60 -and -not $win;$i++){Start-Sleep -Milliseconds 100; $win=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)}
    if(-not $win){throw 'Window did not appear'}
    $buttonCondition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'收起')
    $button=$null
    $handle=[IntPtr]$win.Current.NativeWindowHandle
    for($i=0;$i -lt 60 -and -not $button;$i++){
        Start-Sleep -Milliseconds 100
        $win=[System.Windows.Automation.AutomationElement]::FromHandle($handle)
        $button=$win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$buttonCondition)
    }
    if(-not $button){throw 'Close button missing'}
    Start-Sleep -Milliseconds 300
    $topBefore=$win.Current.BoundingRectangle.Top
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 60
    $visibleImmediately=-not $win.Current.IsOffscreen
    $topDuring=$win.Current.BoundingRectangle.Top
    Start-Sleep -Milliseconds 500
    $visibleAfter=-not $win.Current.IsOffscreen
    if(-not $visibleImmediately){throw 'Window disappeared without an exit animation'}
    if($topDuring -le $topBefore){throw 'Window did not move down during exit'}
    if($visibleAfter){throw 'Window remained visible after exit animation'}
} finally {
    Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $process.Id -and $_.CommandLine -match 'collector.ps1' } | ForEach-Object {Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}
