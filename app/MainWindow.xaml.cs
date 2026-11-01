using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using Forms = System.Windows.Forms;

namespace NetCenter;
public partial class MainWindow : Window
{
    private readonly Forms.NotifyIcon tray = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool exiting;
    private bool hiding;
    private bool modalOpen;
    private DateTime focusGuardUntil = DateTime.MinValue;
    private DateTime lastFocusHide = DateTime.MinValue;
    private int motionVersion;
    private double panelTop;
    private Process? collector;
    private string? defaultAccount; private string autoLink = ""; private bool autoBusy, autoFailed; private DateTime nextAuto = DateTime.MinValue; private int autoGeneration;
    private readonly string python = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python311", "python.exe");
    private readonly string backend = Path.Combine(AppContext.BaseDirectory, "backend", "backend.py");
    private sealed record Profile(string name, string username) { public string Display => $"{name}   ·   {username}"; }
    public MainWindow()
    {
        InitializeComponent();
        var iconPath = @"E:\NetCenter\assets\icon-tray-idle.ico"; tray.Icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Information;
        tray.Text = "NetCenter 网络中心";
        tray.Visible = true;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(TogglePanel); };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开网络中心", null, (_, _) => Dispatcher.Invoke(OpenPanel));
        menu.Items.Add("校园网账号", null, (_, _) => Dispatcher.Invoke(() => { OpenPanel(); ShowCampus(this, new RoutedEventArgs()); }));
        menu.Items.Add("应用热点策略", null, (_, _) => Dispatcher.Invoke(() => { OpenPanel(); _ = RunAction(null, @"E:\MobileHotspotKeeper\Keep-HotspotOn.ps1", "热点策略"); }));
        menu.Items.Add("清除热点手动覆盖", null, (_, _) => Dispatcher.Invoke(ClearHotspotOverride));
        menu.Items.Add("连接蓝牙音频", null, (_, _) => Dispatcher.Invoke(() => { OpenPanel(); _ = RunAction(null, @"E:\NetCenter\tools\bt-connect.ps1", "蓝牙连接"); }));
        menu.Items.Add("清理旧缓存", null, (_, _) => Dispatcher.Invoke(() => { OpenPanel(); _ = RunAction(null, @"E:\NetCenter\tools\clear-stale-cache.ps1", "旧缓存", 10); }));
        menu.Items.Add("打开认证日志", null, (_, _) => Dispatcher.Invoke(OpenCampusLog));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(() => { exiting = true; Close(); }));
        tray.ContextMenuStrip = menu;
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; HidePanel(); } else { timer.Stop(); if (collector is { HasExited: false }) collector.Kill(true); tray.Visible = false; tray.Dispose(); } };
        Loaded += async (_, _) => { if (((App)System.Windows.Application.Current).StartHidden) Hide(); else OpenPanel(); await LoadAccounts(); _ = RunCollector(); };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) HidePanel(); };
        Deactivated += (_, _) =>
        {
            if (!modalOpen && IsVisible && !hiding && DateTime.UtcNow >= focusGuardUntil)
            {
                lastFocusHide = DateTime.UtcNow;
                HidePanel();
            }
        };

    }
    // The popup moves as one surface; the collector continues independently.
    private void OpenPanel()
    {
        var wasHiding = hiding && IsVisible;
        var currentTop = Top;
        var currentOpacity = Opacity;
        ++motionVersion;
        BeginAnimation(TopProperty, null);
        BeginAnimation(OpacityProperty, null);
        hiding = false;
        focusGuardUntil = DateTime.UtcNow.AddMilliseconds(600);
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        var inset = TaskbarInset.Bottom(SystemParameters.PrimaryScreenHeight, area.Bottom, System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY);
        panelTop = area.Bottom - inset - Height - 12;
        if (!SystemParameters.ClientAreaAnimation)
        {
            Top = panelTop; Opacity = 1; Show(); Activate(); return;
        }
        Top = wasHiding ? currentTop : panelTop + 18;
        Opacity = wasHiding ? currentOpacity : 0.05;
        Show(); WindowState = WindowState.Normal; Activate();
        Animate(TopProperty, Top, panelTop);
        Animate(OpacityProperty, Opacity, 1);
    }
    private void Animate(System.Windows.DependencyProperty property, double from, double to, EventHandler? completed = null)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        if (completed != null) animation.Completed += completed;
        BeginAnimation(property, animation);
    }
    private void HidePanel()
    {
        if (!IsVisible || hiding) return;
        var currentTop = Top;
        var currentOpacity = Opacity;
        var version = ++motionVersion;
        BeginAnimation(TopProperty, null);
        BeginAnimation(OpacityProperty, null);
        Top = currentTop; Opacity = currentOpacity;
        hiding = true;
        if (!SystemParameters.ClientAreaAnimation)
        {
            Hide(); hiding = false; Opacity = 1; Top = panelTop; return;
        }
        Animate(TopProperty, currentTop, currentTop + 8);
        Animate(OpacityProperty, currentOpacity, 0.02, (_, _) =>
        {
            if (version != motionVersion) return;
            Hide();
            BeginAnimation(TopProperty, null);
            BeginAnimation(OpacityProperty, null);
            Top = panelTop; Opacity = 1; hiding = false;
        });
    }
    private void TogglePanel()
    {
        if (hiding && (DateTime.UtcNow - lastFocusHide).TotalMilliseconds < 300) return;
        if (hiding || !IsVisible) OpenPanel(); else HidePanel();
    }
    private void HideClick(object sender, RoutedEventArgs e) => HidePanel();
    private void ShowDashboard(object sender, RoutedEventArgs e) => ShowPage(DashboardPage, CampusPage, false);
    private void ShowCampus(object sender, RoutedEventArgs e) => ShowPage(CampusPage, DashboardPage, true);
    private static void ShowPage(System.Windows.FrameworkElement target, System.Windows.FrameworkElement previous, bool forward)
    {
        if (target.Visibility == Visibility.Visible) return;
        previous.BeginAnimation(OpacityProperty, null);
        previous.Opacity = 1;
        previous.Visibility = Visibility.Collapsed;
        target.BeginAnimation(OpacityProperty, null);
        target.Opacity = 1;
        target.Visibility = Visibility.Visible;
        var translate = new System.Windows.Media.TranslateTransform();
        target.RenderTransform = translate;
        if (!SystemParameters.ClientAreaAnimation) return;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0.72, 1, duration) { EasingFunction = easing });
        translate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(forward ? 9 : -9, 0, duration) { EasingFunction = easing });
    }
    private async Task<JsonElement> CallBackend(object request, bool requireSuccess = true)
    {
        var output = await BackendProcessRunner.RunAsync(python, backend, JsonSerializer.Serialize(request));
        using var document = JsonDocument.Parse(output);
        var value = document.RootElement.Clone();
        if (requireSuccess && !value.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(value.TryGetProperty("message", out var msg) ? msg.GetString() : "操作失败");
        return value;
    }
    private async Task LoadAccounts()
    {
        try { var result = await CallBackend(new { action = "list" }); Accounts.ItemsSource = JsonSerializer.Deserialize<List<Profile>>(result.GetProperty("accounts").GetRawText()); var selected = result.GetProperty("default"); defaultAccount = selected.ValueKind == JsonValueKind.Null ? null : selected.GetString(); DefaultAccount.Text = "自动认证账号：" + (defaultAccount ?? "未设置"); }
        catch (Exception ex) { StatusText.Text = "读取账号失败：" + ex.Message; }
    }
    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        var savedName = AccountName.Text.Trim();
        try { await CallBackend(new { action = "save", name = savedName, username = UserId.Text, password = Password.Password }); if (savedName == defaultAccount) ResetAutomaticRetry(); Password.Clear(); await LoadAccounts(); StatusText.Text = "账号已保存"; }
        catch (Exception ex) { StatusText.Text = "保存失败：" + ex.Message; }
    }
    private void AccountSelected(object sender, SelectionChangedEventArgs e) { if (Accounts.SelectedItem is Profile p) { AccountName.Text = p.name; UserId.Text = p.username; Password.Clear(); } }
    private async void DefaultClick(object sender, RoutedEventArgs e)
    {
        if (Accounts.SelectedItem is not Profile p) { StatusText.Text = "请先选择账号"; return; }
        try { await CallBackend(new { action = "select_default", name = p.name }); ResetAutomaticRetry(); await LoadAccounts(); StatusText.Text = "自动认证账号已保存"; }
        catch (Exception ex) { StatusText.Text = "设置失败：" + ex.Message; }
    }
    private async void ClearDefaultClick(object sender, RoutedEventArgs e)
    {
        try { await CallBackend(new { action = "clear_default" }); ResetAutomaticRetry(); await LoadAccounts(); StatusText.Text = "已停止使用默认账号自动认证"; }
        catch (Exception ex) { StatusText.Text = "操作失败：" + ex.Message; }
    }
    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (Accounts.SelectedItem is not Profile p) { StatusText.Text = "请先选择账号"; return; }
        MessageBoxResult confirmed;
        modalOpen = true;
        try { confirmed = System.Windows.MessageBox.Show(this, $"删除账号“{p.name}”？", "删除账号", MessageBoxButton.YesNo, MessageBoxImage.Question); }
        finally { modalOpen = false; focusGuardUntil = DateTime.UtcNow.AddMilliseconds(300); }
        if (confirmed != MessageBoxResult.Yes) return;
        try { var deletedDefault = p.name == defaultAccount; await CallBackend(new { action = "delete", name = p.name }); if (deletedDefault) ResetAutomaticRetry(); await LoadAccounts(); Password.Clear(); StatusText.Text = "账号已删除"; } catch (Exception ex) { StatusText.Text = "删除失败：" + ex.Message; }
    }
    private async void AuthenticateClick(object sender, RoutedEventArgs e)
    {
        if (Accounts.SelectedItem is not Profile p) { StatusText.Text = "请先选择账号"; return; }
        var button = (System.Windows.Controls.Button)sender; button.IsEnabled = false; StatusText.Text = "正在检查并认证…";
        try { var result = await CallBackend(new { action = "auth", name = p.name }); StatusText.Text = result.GetProperty("message").GetString() ?? "认证完成"; }
        catch (Exception ex) { StatusText.Text = "认证未完成：" + ex.Message; }
        finally { button.IsEnabled = true; }
    }
    private async void HotspotClick(object sender, RoutedEventArgs e) => await RunAction(sender, @"E:\MobileHotspotKeeper\Keep-HotspotOn.ps1", "热点策略");
    private async void BluetoothClick(object sender, RoutedEventArgs e) => await RunAction(sender, @"E:\NetCenter\tools\bt-connect.ps1", "蓝牙连接");
    private async Task RunAction(object? sender, string script, string label, int? additionalSuccessCode = null)
    {
        var button = sender as System.Windows.Controls.Button;
        if (button != null) button.IsEnabled = false;
        StatusText.Text = label + "处理中…";
        try {
            if (!File.Exists(script)) throw new FileNotFoundException("找不到操作脚本", script);
            var result = await PowerShellActionRunner.RunAsync(script);
            StatusText.Text = PowerShellActionRunner.Describe(label, result, additionalSuccessCode);
        } catch (Exception ex) { StatusText.Text = label + "失败：" + ex.Message; }
        finally { if (button != null) button.IsEnabled = true; }
    }
    private async void ClearHotspotOverride()
    {
        OpenPanel();
        MessageBoxResult confirmed;
        modalOpen = true;
        try
        {
            confirmed = System.Windows.MessageBox.Show(this, "清除手动覆盖后将按当前链路重新执行热点策略，热点状态可能变化。继续吗？", "清除热点手动覆盖", MessageBoxButton.YesNo, MessageBoxImage.Question);
        }
        finally { modalOpen = false; focusGuardUntil = DateTime.UtcNow.AddMilliseconds(300); }
        if (confirmed != MessageBoxResult.Yes) return;
        var script = Path.Combine(AppContext.BaseDirectory, "backend", "clear_hotspot_override.ps1");
        await RunAction(null, script, "清除热点覆盖");
    }
    private void OpenCampusLog()
    {
        var file = @"E:\CampusAuth\campus_auth.log";
        if (!File.Exists(file)) { OpenPanel(); StatusText.Text = "找不到校园认证日志"; return; }
        try
        {
            var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
            start.ArgumentList.Add(file);
            Process.Start(start);
        }
        catch (Exception ex) { OpenPanel(); StatusText.Text = "打开日志失败：" + ex.Message; }
    }
    private async Task RunCollector()
    {
        try {
            var exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "backend", "collector.ps1") }) start.ArgumentList.Add(arg);
            using var owner = Process.GetCurrentProcess();
            start.ArgumentList.Add("-ParentPid"); start.ArgumentList.Add(owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-ParentStartTicks"); start.ArgumentList.Add(owner.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            collector = Process.Start(start) ?? throw new InvalidOperationException("无法启动采集程序");
            _ = collector.StandardError.ReadToEndAsync();
            string? line;
            while ((line = await collector.StandardOutput.ReadLineAsync()) != null) {
                using var doc = JsonDocument.Parse(line);
                ShowSnapshot(doc.RootElement); _ = MaybeAuto(doc.RootElement.Clone());
            }
            if (!exiting) StatusText.Text = "网络采集已停止，请重启程序";
        } catch (Exception ex) { if (!exiting) StatusText.Text = "状态采集失败：" + ex.Message; }
    }
    private void ResetAutomaticRetry() { ++autoGeneration; autoFailed = false; nextAuto = DateTime.MinValue; }
    private async Task MaybeAuto(JsonElement state)
    {
        var link = state.GetProperty("Link").GetProperty("Sig").GetString() ?? "";
        if (link != autoLink) { autoLink = link; ResetAutomaticRetry(); }
        var code = state.GetProperty("Portal").GetProperty("Code").GetString();
        if (code != "walled" || defaultAccount == null || autoBusy || autoFailed || DateTime.UtcNow < nextAuto) return;
        autoBusy = true; nextAuto = DateTime.UtcNow.AddMinutes(2); var generation = autoGeneration;
        try {
            var result = await CallBackend(new { action = "auto" }, false);
            if (generation != autoGeneration) return;
            var status = result.TryGetProperty("code", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt32() : -1;
            var message = result.TryGetProperty("message", out var text) ? text.GetString() : "未知错误";
            if (status == 1 || status == 3 || status == -1) autoFailed = true;
            StatusText.Text = status == 0 ? "自动认证：" + message : status == 4 ? "其他认证正在进行" : "自动认证：" + message;
        } catch (Exception ex) { if (generation == autoGeneration) { autoFailed = true; StatusText.Text = "自动认证失败：" + ex.Message; } }
        finally { autoBusy = false; }
    }
    private void ShowSnapshot(JsonElement state)
    {
        string Get(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";
        var link=state.GetProperty("Link"); var portal=state.GetProperty("Portal"); var hot=state.GetProperty("Hotspot");
        var clash=state.GetProperty("Clash"); var speed=state.GetProperty("Speed"); var gpt=state.GetProperty("Gpt"); var egress=state.GetProperty("Egress");
        LinkText.Text = Get(link, "Summary");
        var ip = Get(link, "EthIp"); if (string.IsNullOrEmpty(ip)) ip = Get(link, "WifiIp");
        var route = Get(link, "DefaultRoute");
        LinkDetail.Text = string.IsNullOrEmpty(ip) ? "暂无地址" : $"本机 {ip}  ·  {route}";
        LinkDetail.ToolTip = LinkDetail.Text;
        SpeedDown.Text = "↓ " + Get(speed, "RxText"); SpeedUp.Text = "↑ " + Get(speed, "TxText");
        PortalText.Text = Get(portal, "Text");
        var portalMs = Get(portal, "Ms");
        var session = state.GetProperty("Session");
        var campusIps = link.TryGetProperty("CampusIps", out var ips) && ips.ValueKind == JsonValueKind.Array
            ? ips.EnumerateArray().Select(item => item.ToString()).ToArray()
            : Array.Empty<string>();
        var campusAddress = CampusSessionDisplay.Choose(Get(session, "SessionIp"), campusIps);
        PortalDetail.Text = $"门户 {(portalMs == "-1" ? "--" : portalMs + "ms")}  ·  内网IP {(campusAddress.Address.Length == 0 ? "--" : campusAddress.Address)}" + (campusAddress.IsStale ? "  ·  旧缓存" : "");
        var hotState = Get(hot, "State"); var desired = Get(hot, "Desired"); var manual = Get(hot, "Manual");
        HotspotText.Text = HotspotDisplay.State(hotState);
        HotspotDetail.Text = $"期望 {(desired.Length == 0 ? "保持" : desired)}  ·  客户端 {Get(hot, "Clients")} 台" + (manual.Length == 0 ? "" : "  ·  手动覆盖");
        bool core = Get(clash, "Core").Equals("True", StringComparison.OrdinalIgnoreCase);
        bool tun = Get(clash, "Tun").Equals("True", StringComparison.OrdinalIgnoreCase);
        bool HasLink(string key) => Get(link, key).Equals("True", StringComparison.OrdinalIgnoreCase);
        System.Windows.Media.Brush StatusBrush(string severity) => (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(severity switch { "ok" => "#6CCB5F", "warn" => "#FFB900", "err" => "#FF6363", _ => "#8A8A8A" })!;
        LinkAccent.Background = StatusBrush(HasLink("EthUp") || HasLink("WifiUp") ? "ok" : "err");
        PortalAccent.Background = StatusBrush(Get(portal, "Severity"));
        var hotspotSeverity = hotState is "Error" or "NoInternet" or "InTransition" || manual.Length > 0 || (desired.Length > 0 && hotState is "On" or "Off" && hotState != desired) ? "warn" : hotState is "On" or "Off" ? "ok" : "idle";
        HotspotAccent.Background = StatusBrush(hotspotSeverity);
        ClashAccent.Background = StatusBrush(core ? "ok" : "warn");
        ClashText.Text = (core ? "内核运行中" : "内核未运行") + "  ·  TUN " + (tun ? "开" : "关");
        ClashDetail.Text = "ChatGPT " + Get(gpt, "Text");
        var direct = Get(egress, "Direct"); var proxy = Get(egress, "Proxy");
        DetailText.Text = $"出口 {(direct.Length == 0 ? "检测中…" : direct)}  ·  代理 {(tun ? (proxy.Length == 0 ? "检测中…" : proxy) : "未启用")}";
        DetailText.ToolTip = DetailText.Text;
        var overall = Get(state, "Overall");
        var color = overall switch { "ok" => "#6CCB5F", "warn" => "#FFB900", "err" => "#FF6363", _ => "#8A8A8A" };
        OverallText.Text = overall switch { "ok" => "正常", "warn" => "注意", "err" => "异常", _ => "空闲" };
        OverallBadge.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
        if (StatusText.Text == "正在更新网络状态…") StatusText.Text = "每秒更新  ·  门户探测 10 秒";
        var severity=Get(state,"Overall"); var iconFile=Path.Combine(@"E:\NetCenter\assets", $"icon-tray-{severity}.ico");
        if (File.Exists(iconFile)) { var oldIcon=tray.Icon; tray.Icon=new System.Drawing.Icon(iconFile); if (oldIcon != System.Drawing.SystemIcons.Information) oldIcon?.Dispose(); }
    }
}
