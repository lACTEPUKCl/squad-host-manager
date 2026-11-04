using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;
using SquadHostManager.Desktop;
using Localization=SquadHostManager.Desktop.Localization;
internal static class ConnectionStatusChecks
{
    public static void Run(MainWindow window)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var session=(SessionResponse)typeof(MainWindow).GetField("session",flags)!.GetValue(window)!;
        typeof(MainWindow).GetField("hub",flags)!.SetValue(window,null);
        typeof(MainWindow).GetMethod("RenderPool",flags)!.Invoke(window,null);
        var status=(TextBlock)window.FindName("Status");
        if(status.Text!=Localization.T("connected",session.Name))throw new Exception("Successful HTTP snapshot shows reconnection instead of the signed-in account");
        status.Measure(new Size(192,double.PositiveInfinity));var height=status.DesiredSize.Height;
        status.Text="A temporary connection message long enough to wrap across several lines and shift the whole workspace";
        status.InvalidateMeasure();status.Measure(new Size(192,double.PositiveInfinity));
        if(Math.Abs(status.DesiredSize.Height-height)>.1)throw new Exception("Footer message changes the workspace height");
        typeof(MainWindow).GetMethod("RenderPool",flags)!.Invoke(window,null);
        Console.WriteLine("PASS: polling preserves signed-in status; footer messages do not move the workspace");
    }
}
