using System.Text;
using NetCenter;

var dir = Path.Combine(Path.GetTempPath(), "netcenter-action-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try {
    var script = Path.Combine(dir, "test'中文.ps1");
    await File.WriteAllTextAsync(script, "Write-Host '连接失败'\nexit 7\n", new UTF8Encoding(true));
    var failure = await PowerShellActionRunner.RunAsync(script);
    if (failure.ExitCode != 7 || !failure.Stdout.Contains("连接失败")) { Console.WriteLine($"Failure exit={failure.ExitCode}, stdout={failure.Stdout}, stderr={failure.Stderr}"); Environment.ExitCode=1; return; }
    if (!PowerShellActionRunner.Describe("蓝牙连接", failure).Contains("连接失败")) throw new Exception("Failure description hides the useful output");
    await File.WriteAllTextAsync(script, "Write-Output '正在准备'\nthrow '设备不可用'\n", new UTF8Encoding(true));
    var thrown = await PowerShellActionRunner.RunAsync(script);
    if (thrown.ExitCode == 0 || !PowerShellActionRunner.Describe("蓝牙连接", thrown).Contains("设备不可用"))
        throw new Exception($"Failure description hides the error: {PowerShellActionRunner.Describe("蓝牙连接", thrown)}; stderr={thrown.Stderr}");
    await File.WriteAllTextAsync(script, "Write-Host '操作完成'\nexit 0\n", new UTF8Encoding(true));
    var success = await PowerShellActionRunner.RunAsync(script);
    if (success.ExitCode != 0 || !PowerShellActionRunner.Describe("测试", success).Contains("操作完成")) throw new Exception("Successful Chinese output was lost");
    await File.WriteAllTextAsync(script, "Write-Host '已隔离'\nexit 10\n", new UTF8Encoding(true));
    var isolated = await PowerShellActionRunner.RunAsync(script);
    if (!PowerShellActionRunner.Describe("缓存", isolated, 10).Contains("已隔离")) throw new Exception("Expected cache quarantine exit code was treated as an error");
    if (!PowerShellActionRunner.Describe("缓存", isolated).Contains("失败")) throw new Exception("Unexpected nonzero exit code was accepted");
    await File.WriteAllTextAsync(script, "Start-Sleep -Seconds 30\n", new UTF8Encoding(true));
    var timedOut = false;
    try { await PowerShellActionRunner.RunAsync(script, TimeSpan.FromMilliseconds(500)); }
    catch (TimeoutException) { timedOut = true; }
    if (!timedOut) throw new Exception("Hung script was not stopped after the timeout");
    var appDir = Path.Combine(dir, "网络中心 with spaces");
    Directory.CreateDirectory(Path.Combine(appDir, "bluetooth"));
    var receiver = Path.Combine(appDir, "bluetooth", "BluetoothAudioReceiver.exe");
    try { BluetoothReceiverLauncher.ResolveExecutable(appDir); throw new Exception("Missing receiver was accepted"); }
    catch (FileNotFoundException) { }
    await File.WriteAllTextAsync(receiver, "test fixture");
    if (BluetoothReceiverLauncher.ResolveExecutable(appDir) != receiver) throw new Exception("Bundled receiver path is incorrect");
    File.Delete(receiver); Directory.Delete(Path.Combine(appDir, "bluetooth")); Directory.Delete(appDir);
    if (TaskbarInset.Calculate(false, 3, 48, 1, 1080, 1080) != 0 || TaskbarInset.Calculate(true, 0, 48, 1, 1080, 1080) != 0 || TaskbarInset.Calculate(true, 3, 48, 1, 1080, 1032) != 0) throw new Exception("Reserved space for a taskbar that is already accounted for");
    if (TaskbarInset.Calculate(true, 3, 60, 1.25, 864, 862) != 46 || TaskbarInset.Calculate(true, 3, 2, 1, 1080, 1080) != 48) throw new Exception("Auto-hidden bottom taskbar reserve is incorrect");
    var python = Environment.GetEnvironmentVariable("NETCENTER_TEST_PYTHON") ?? throw new Exception("Test Python path missing");
    var backend = Path.Combine(dir, "fake-backend.py");
    await File.WriteAllTextAsync(backend, "import sys\nsys.stderr.write('x'*200000)\nsys.stderr.flush()\nrequest=sys.stdin.read()\nprint(request)\n", new UTF8Encoding(false));
    var backendTask = BackendProcessRunner.RunAsync(python, backend, "{\"ok\":true}");
    if (await Task.WhenAny(backendTask, Task.Delay(5000)) != backendTask) throw new Exception("Backend deadlocked while stderr filled");
    if ((await backendTask).Trim() != "{\"ok\":true}") throw new Exception("Backend output was lost");
    await File.WriteAllTextAsync(backend, "import sys\nsys.stdin.read()\nsys.stderr.write('认证服务出错')\nsys.exit(2)\n", new UTF8Encoding(false));
    try { await BackendProcessRunner.RunAsync(python, backend, "{}"); throw new Exception("Nonzero backend exit was accepted"); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("认证服务出错")) { }
    var valid = CampusSessionDisplay.Choose("172.30.1.2", new[] { "172.30.1.2" });
    if (HotspotDisplay.State("InTransition") != "切换中" || HotspotDisplay.State("NoInternet") != "无可共享连接" || HotspotDisplay.State("On") != "已开启" || HotspotDisplay.State("Off") != "已关闭" || HotspotDisplay.State("Error") != "状态读取失败" || HotspotDisplay.State("other") != "状态未知") throw new Exception("Hotspot states were not distinguished");
    if (valid.Address != "172.30.1.2" || valid.IsStale) throw new Exception("Current cached session should be displayed");
    var stale = CampusSessionDisplay.Choose("172.30.2.3", new[] { "172.30.1.2" });
    if (stale.Address != "172.30.1.2" || !stale.IsStale) throw new Exception("Stale cache should use the current campus address");
    var offline = CampusSessionDisplay.Choose("172.30.2.3", Array.Empty<string>());
    if (offline.Address != "" || !offline.IsStale) throw new Exception("Stale cache should not appear as a current address when offline");
    Console.WriteLine("Action runner checks passed");
} finally {
    foreach(var file in Directory.GetFiles(dir)) File.Delete(file);
    Directory.Delete(dir);
}
