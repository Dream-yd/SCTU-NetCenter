using System.Threading;
using System.Windows;

namespace NetCenter;
public partial class App : System.Windows.Application
{
    public bool StartHidden { get; private set; }
    private Mutex? instanceMutex;
    private bool ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        var key = Environment.GetEnvironmentVariable("NETCENTER_INSTANCE_KEY");
        if (string.IsNullOrWhiteSpace(key)) key = "NetCenterNext";
        instanceMutex = new Mutex(true, @"Local\" + key, out ownsMutex);
        if (!ownsMutex) { Shutdown(0); return; }
        StartHidden = e.Args.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
