using System.Diagnostics;
using System.IO;

namespace NetCenter;

internal static class BluetoothReceiverLauncher
{
    public static string ResolveExecutable(string baseDirectory)
    {
        var executable = Path.Combine(baseDirectory, "bluetooth", "BluetoothAudioReceiver.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("蓝牙音频组件缺失，请使用完整发行包或重新构建。", executable);
        return executable;
    }

    public static void Open(string baseDirectory)
    {
        var executable = ResolveExecutable(baseDirectory);
        Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        });
    }
}
