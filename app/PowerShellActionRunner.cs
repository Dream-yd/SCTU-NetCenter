using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace NetCenter;

internal sealed record PowerShellActionResult(int ExitCode, string Stdout, string Stderr);

internal static class PowerShellActionRunner
{
    public static async Task<PowerShellActionResult> RunAsync(string script, TimeSpan? timeout = null)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var quotedPath = script.Replace("'", "''");
        var command = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
                      "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); " +
                      "try { & '" + quotedPath + "'; $succeeded=$?; if (-not $succeeded) { if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1 } } " +
                      "catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-EncodedCommand", encoded })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 PowerShell");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeoutSource.Token); }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            throw new TimeoutException("操作超时，已停止后台进程");
        }
        return new PowerShellActionResult(process.ExitCode, (await stdout).Trim(), (await stderr).Trim());
    }

    public static string Describe(string label, PowerShellActionResult result, int? additionalSuccessCode = null)
    {
        var failed = result.ExitCode != 0 && result.ExitCode != additionalSuccessCode;
        var detail = failed && !string.IsNullOrWhiteSpace(result.Stderr) ? result.Stderr :
                     !string.IsNullOrWhiteSpace(result.Stdout) ? result.Stdout : result.Stderr;
        var lastLine = detail.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0);
        if (failed)
            return $"{label}失败：{(string.IsNullOrEmpty(lastLine) ? "无详细信息" : lastLine)}（退出码 {result.ExitCode}）";
        return string.IsNullOrEmpty(lastLine) ? label + "已完成" : lastLine;
    }
}
