$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient, System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class FocusNative {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
}
"@
$exe=$env:NETCENTER_TEST_EXE
if(-not $exe){throw 'NETCENTER_TEST_EXE is required'}
$process=Start-Process -FilePath $exe -PassThru
$form=$null
try {
 $pidCondition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
 $nameCondition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'NetCenter 网络中心')
 $condition=New-Object System.Windows.Automation.AndCondition($pidCondition,$nameCondition)
 $win=$null; for($i=0;$i -lt 80 -and -not $win;$i++){Start-Sleep -Milliseconds 100;$win=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)}
 if(-not $win){throw 'Panel did not appear'}
 [void][FocusNative]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle)
 Start-Sleep -Seconds 2
 if([FocusNative]::GetForegroundWindow() -ne [IntPtr]$win.Current.NativeWindowHandle){throw 'Could not activate the NetCenter panel'}
 $form=New-Object System.Windows.Forms.Form
 $form.Text='NetCenter focus-test'; $form.ShowInTaskbar=$false; $form.StartPosition='Manual'; $form.Location=New-Object System.Drawing.Point(-3000,-3000)
 $form.Show(); [System.Windows.Forms.Application]::DoEvents(); $form.Activate(); [void][FocusNative]::SetForegroundWindow($form.Handle); [System.Windows.Forms.Application]::DoEvents()
 if([FocusNative]::GetForegroundWindow() -ne $form.Handle){throw 'Could not activate the test window'}
 $hidden=$false
 for($i=0;$i -lt 50;$i++){Start-Sleep -Milliseconds 100; if(-not [FocusNative]::IsWindowVisible([IntPtr]$win.Current.NativeWindowHandle)){$hidden=$true;break}}
 if(-not $hidden){throw 'Panel stayed visible after losing focus'}
} finally {
 if($form){$form.Close();$form.Dispose()}
 Get-CimInstance Win32_Process | Where-Object {$_.ParentProcessId -eq $process.Id -and $_.CommandLine -match 'collector.ps1'} | ForEach-Object {Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}
 Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}
