using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;
using SquadHostManager.Desktop;
using Localization=SquadHostManager.Desktop.Localization;
internal static class ProviderChecks
{
    public static void Run(MainWindow window)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        void Set(string name,object value)=>typeof(MainWindow).GetField(name,flags)!.SetValue(window,value);
        T Field<T>(string name)=>(T)typeof(MainWindow).GetField(name,flags)!.GetValue(window)!;
        void Call(string name)=>typeof(MainWindow).GetMethod(name,flags)!.Invoke(window,null);
        void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
        Call("BuildHostingProviders");
        Set("hostingProvider",new HostingProviderView("cherry",false,true));Call("RenderHostingProvider");
        Assert(((FrameworkElement)window.FindName("ProviderSelectorPanel")).Visibility==Visibility.Collapsed,"Switch visible with active servers");
        Set("hostingProvider",new HostingProviderView("cherry",true,true));Call("RenderHostingProvider");
        Assert(Field<Button>("providerApply").IsEnabled,"Switch disabled after all servers stopped");
        Assert(((FrameworkElement)window.FindName("ProviderSelectorPanel")).Visibility==Visibility.Visible,"Switch hidden with no servers");
        Assert(!((StackPanel)window.FindName("ProviderSelectorPanel")).Children.OfType<ComboBox>().Any(),"Provider dropdown remains visible");
        Set("hostingProvider",new HostingProviderView("respawn",false,true));Call("RenderHostingProvider");
        Assert(((FrameworkElement)window.FindName("HostingLayout")).Visibility==Visibility.Collapsed,"Cherry controls visible in Respawn mode");
        Assert(((FrameworkElement)window.FindName("RespawnHostingPanel")).Visibility==Visibility.Visible,"Respawn controls hidden");
        Assert(!((StackPanel)window.FindName("RespawnHostingPanel")).Children.OfType<Control>().Any(c=>c is ComboBox or PasswordBox),"Respawn still asks for a key, plan or region");
        Assert(((FrameworkElement)window.FindName("AccountBalance")).Visibility==Visibility.Collapsed,"Respawn balance remains visible");
        Assert(((FrameworkElement)window.FindName("OverviewBillingNote")).Visibility==Visibility.Visible,"Missing per-second billing note");
        Assert(!((Button)window.FindName("RentHostButton")).IsEnabled,"Cherry host rental remains enabled in Respawn mode");
        Set("pool",new PoolView(new("respawn:test","respawnhost.com","ready",null),[new("managed-instance","owner","Owner","Managed Squad",0,PortSet.ForSlot(0),"running",true,null,"gameReady",Address:"203.0.113.8:7787")],false,false,null));
        Set("selectedId","managed-instance");Call("RenderPool");Call("RenderHostingProvider");
        Assert(((Button)window.FindName("RestartButton")).IsEnabled,"Managed restart disabled");
        Assert(((Button)window.FindName("CommandButton")).IsEnabled,"Managed RCON disabled");
        Assert(((Button)window.FindName("ArchiveButton")).IsEnabled,"Managed archive disabled");
        Assert(((TextBlock)window.FindName("MineAddress")).Text=="203.0.113.8:7787","Managed endpoint replaced by shared Cherry ports");
        Set("draft",true);Set("selectedId",null!);
        ((TabControl)window.FindName("Tabs")).SelectedItem=window.FindName("ConsoleTab");Call("RenderPool");
        Assert(!Field<bool>("draft"),"New-server draft blocks management of the running server");
        Assert(Field<string>("selectedId")=="managed-instance","Running server not selected after leaving draft");
        Assert(((Button)window.FindName("CommandButton")).IsEnabled,"RCON remains disabled despite gameReady");
        Call("RestoreServerDraft");
        Assert(Field<string>("selectedId")=="managed-instance" && !Field<bool>("draft"),"Draft restore replaced the active server");
        foreach(var language in Localization.Languages){Localization.Apply(language,false);Assert(Localization.T("providerSwitchBlocked")!="providerSwitchBlocked","Missing provider translation");}
        Localization.Apply("ru",false);
        Console.WriteLine("PASS: WPF hosting switch disabled while servers exist; Respawn mode; all seven languages");
    }
}
