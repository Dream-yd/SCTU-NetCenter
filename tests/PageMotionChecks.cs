using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal static class PageMotionChecks
{
    [STAThread]
    private static void Main()
    {
        var method = typeof(NetCenter.MainWindow).GetMethod("ShowPage", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new Exception("Page transition is missing");
        var dashboard = new Grid { Visibility = Visibility.Visible };
        var accounts = new ScrollViewer { Visibility = Visibility.Collapsed };
        for (var i = 0; i < 4; i++)
        {
            method.Invoke(null, new object[] { accounts, dashboard, true });
            if (accounts.Visibility != Visibility.Visible || dashboard.Visibility != Visibility.Collapsed)
                throw new Exception("Accounts page was not shown");
            if (accounts.RenderTransform is not TranslateTransform)
                throw new Exception("Page transition has no translation transform");
            if (SystemParameters.ClientAreaAnimation && (!accounts.HasAnimatedProperties || !accounts.RenderTransform.HasAnimatedProperties))
                throw new Exception("Enabled motion did not animate the entering page");
            method.Invoke(null, new object[] { dashboard, accounts, false });
            if (dashboard.Visibility != Visibility.Visible || accounts.Visibility != Visibility.Collapsed)
                throw new Exception("Dashboard was not restored after interruption");
        }
        method.Invoke(null, new object[] { accounts, dashboard, true });
        if (accounts.Visibility != Visibility.Visible || dashboard.Visibility != Visibility.Collapsed)
            throw new Exception("Final navigation target was lost");
        Console.WriteLine("Page motion checks passed");
    }
}
