using System.Diagnostics;
using System.Text;

namespace NetCenter;

internal static class BackendProcessRunner
{
    public static async Task<string> RunAsync(string python, string backend, string request)
    {
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.ArgumentList.Add(backend);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动校园网服务");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(request);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output;
    }
}
