$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public delegate bool TestEnumProc(IntPtr h, IntPtr arg);
public static class TestWindows {
 [DllImport("user32.dll")] public static extern bool EnumWindows(TestEnumProc callback, IntPtr arg);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder text, int max);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint command);
 [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint message, IntPtr wparam, IntPtr lparam);
 public static IntPtr FindWindowForProcess(uint target, string title) {
   IntPtr found=IntPtr.Zero;
   EnumWindows((h, arg) => { uint pid; GetWindowThreadProcessId(h, out pid); var text=new StringBuilder(512); GetWindowText(h,text,512); if(pid==target && IsWindowVisible(h) && text.ToString()==title) { found=h; return false; } return true; }, IntPtr.Zero);
   return found;
 }
}
"@
$exe=if($env:NETCENTER_TEST_EXE){$env:NETCENTER_TEST_EXE}else{Join-Path (Split-Path $PSScriptRoot -Parent) 'app\bin\Debug\net8.0-windows\NetCenter.exe'}
$process=Start-Process -FilePath $exe -PassThru
function Find-Element($container,$property,$value,$maxAttempts=80){
 $condition=New-Object System.Windows.Automation.PropertyCondition($property,$value)
 $handle=[IntPtr]$container.Current.NativeWindowHandle
 for($i=0;$i -lt $maxAttempts;$i++){
  $fresh=if($handle -ne [IntPtr]::Zero){[System.Windows.Automation.AutomationElement]::FromHandle($handle)}else{$container}
  $element=$fresh.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
  if($element){return $element}
  Start-Sleep -Milliseconds 100
 }
 throw "UI element not found: $value"
}
function Invoke-ByName($container,$name){$button=Find-Element $container ([System.Windows.Automation.AutomationElement]::NameProperty) $name; $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Set-ById($container,$id,$value){$element=Find-Element $container ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) $id; $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)}
function Wait-Until($condition,$message){for($i=0;$i -lt 100;$i++){if(& $condition){return};Start-Sleep -Milliseconds 100};throw $message}
try {
 $pidCondition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
 $nameCondition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'NetCenter 网络中心')
 $condition=New-Object System.Windows.Automation.AndCondition($pidCondition,$nameCondition)
 $window=$null; for($i=0;$i -lt 80 -and -not $window;$i++){Start-Sleep -Milliseconds 100; $window=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)}
 if(-not $window){throw 'Window did not appear'}
 [void][TestWindows]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle)
 if([TestWindows]::GetForegroundWindow() -ne [IntPtr]$window.Current.NativeWindowHandle){throw 'Could not activate the test panel'}
 Invoke-ByName $window '校园网账号与认证'
 $store=Join-Path $env:NETCENTER_DATA_DIR 'accounts.json'
 for($index=1;$index -le 2;$index++){
  Set-ById $window 'AccountName' "profile$index"
  Set-ById $window 'UserId' "student$index"
  Set-ById $window 'Password' "secret$index"
  Invoke-ByName $window '保存账号'
  Wait-Until { if(-not (Test-Path -LiteralPath $store)){return $false}; try {$json=Get-Content -LiteralPath $store -Raw -Encoding UTF8 | ConvertFrom-Json; return @($json.PSObject.Properties).Count -eq $index}catch{return $false} } 'Account save did not finish'
 }
 $raw=Get-Content -LiteralPath $store -Raw -Encoding UTF8
 if($raw -match 'secret1|secret2'){throw 'Passwords appeared as plaintext'}
 $item=Find-Element $window ([System.Windows.Automation.AutomationElement]::NameProperty) 'profile2   ·   student2'
 $walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker
 $selection=$null
 while($item -and -not $selection){if($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selection)){break}; $item=$walker.GetParent($item)}
 if(-not $selection){throw 'Account list item is not selectable'}
 $selection.Select()
 Invoke-ByName $window '设为自动认证账号'
 $defaults=Join-Path $env:NETCENTER_DATA_DIR 'default.json'
 Wait-Until {if(-not (Test-Path -LiteralPath $defaults)){return $false}; try{return (Get-Content -LiteralPath $defaults -Raw -Encoding UTF8 | ConvertFrom-Json).name -eq 'profile2'}catch{return $false}} 'Default was not selected'
 if($env:NETCENTER_TEST_RETRY_FILE){
  Wait-Until { (Test-Path -LiteralPath $env:NETCENTER_TEST_RETRY_FILE) -and [int](Get-Content -LiteralPath $env:NETCENTER_TEST_RETRY_FILE -Raw) -ge 1 } 'Initial simulated authentication did not run'
  if(-not $env:NETCENTER_TEST_RELEASE_FILE){
   Wait-Until { try { $status=Find-Element $window ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) 'StatusText' 1; return $status.Current.Name -like '自动认证：*' } catch { return $false } } 'Initial failure was not displayed'
  }
  Set-ById $window 'AccountName' 'profile2'
  Set-ById $window 'UserId' 'student2'
  Set-ById $window 'Password' 'corrected-password'
  Invoke-ByName $window '保存账号'
  if($env:NETCENTER_TEST_RELEASE_FILE){
   Wait-Until { try { $status=Find-Element $window ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) 'StatusText' 1; return $status.Current.Name -eq '账号已保存' } catch { return $false } } 'Corrected profile was not saved while authentication was pending'
   [IO.File]::WriteAllText($env:NETCENTER_TEST_RELEASE_FILE,'release')
  }
  Wait-Until { (Test-Path -LiteralPath $env:NETCENTER_TEST_RETRY_FILE) -and [int](Get-Content -LiteralPath $env:NETCENTER_TEST_RETRY_FILE -Raw) -ge 2 } 'Corrected default profile did not trigger a retry'
 }
 Invoke-ByName $window '停止自动认证'
 Wait-Until {try{return $null -eq (Get-Content -LiteralPath $defaults -Raw -Encoding UTF8 | ConvertFrom-Json).name}catch{return $false}} 'Default was not cleared'
 Wait-Until { try { $status=Find-Element $window ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) 'StatusText' 1; return $status.Current.Name -like '已停止*' } catch { return $false } } 'Account list did not finish refreshing'
 $item=Find-Element $window ([System.Windows.Automation.AutomationElement]::NameProperty) 'profile2   ·   student2'
 $selection=$null
 while($item -and -not $selection){if($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selection)){break};$item=$walker.GetParent($item)}
 if(-not $selection){throw 'Account list item is not selectable after reload'}
 $selection.Select()
 $del=Find-Element $window ([System.Windows.Automation.AutomationElement]::NameProperty) '删除'
 $del.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $dialogHandle=[IntPtr]::Zero
 for($i=0;$i -lt 50 -and $dialogHandle -eq [IntPtr]::Zero;$i++){Start-Sleep -Milliseconds 100; $dialogHandle=[TestWindows]::FindWindowForProcess([uint32]$process.Id,'删除账号')}
 if($dialogHandle -eq [IntPtr]::Zero){throw 'Delete confirmation did not appear'}
 $owner=[TestWindows]::GetWindow($dialogHandle,4)
 if($owner -ne [IntPtr]$window.Current.NativeWindowHandle){throw 'Delete confirmation is not owned by the panel'}
 $dialog=[System.Windows.Automation.AutomationElement]::FromHandle($dialogHandle)
 $yes=[TestWindows]::GetDlgItem($dialogHandle,6)
 if($yes -eq [IntPtr]::Zero){throw 'Delete confirmation button was not found'}
 [void][TestWindows]::SendMessage($yes,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero)
 Wait-Until {try{$json=Get-Content -LiteralPath $store -Raw -Encoding UTF8 | ConvertFrom-Json;return @($json.PSObject.Properties).Count -eq 1}catch{return $false}} 'Account delete did not finish'
 Write-Output 'Account UI checks passed'
 } catch {
 try {
  $process.Refresh()
  $fresh=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)
  $nodes=if($fresh){$fresh.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)}else{$null}
  $names=if($nodes){@($nodes | ForEach-Object {$_.Current.Name} | Where-Object {$_} | Select-Object -First 20)}else{@()}
  $storeRaw=if($store -and (Test-Path -LiteralPath $store)){Get-Content -LiteralPath $store -Raw -Encoding UTF8}else{'missing'}
  Write-Output ('DIAGNOSTIC exited='+$process.HasExited+' originalHandle='+$window.Current.NativeWindowHandle+' nativeVisible='+[TestWindows]::IsWindowVisible([IntPtr]$window.Current.NativeWindowHandle)+' foreground='+[TestWindows]::GetForegroundWindow()+' fresh='+[bool]$fresh+' childCount='+$(if($nodes){$nodes.Count}else{0})+' names='+($names -join '|')+' store='+$storeRaw)
 } catch { Write-Output ('DIAGNOSTIC FAILURE '+$_.Exception.Message) }

 throw
} finally {
 Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $process.Id -and $_.CommandLine -match 'collector.ps1' } | ForEach-Object {Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}
 Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
 try { $process.WaitForExit(5000) | Out-Null } catch { }
}
