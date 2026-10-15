using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SquadHostManager.Core;
using SquadHostManager.Desktop;

internal static class RconChecks
{
    private sealed class Replies:HttpMessageHandler
    {
        public List<string> Calls=[];
        public string Reply="Kicked player 3.";
        public const string Catalog="ListPlayers (Players)\nListSquads (Squads)\nShowCurrentMap (Current map)\nShowNextMap (Next map)\nListLayers (Layers)\nAdminKick \"<NameOrSteamId>\" <KickReason> (Kick)\nAdminBan \"<NameOrEOSId>\" \"<BanLength>\" <BanReason> (Ban)\nAdminWarn \"<NameOrEOSId>\" <WarnReason> (Warn)\nAdminForceTeamChange \"<NameOrSteamId>\" (Switch)\nAdminKickById <PlayerId> <KickReason> (Kick by ID)\nAdminBanById <PlayerId> <BanLength> <BanReason> (Ban by ID)\nAdminWarnById <PlayerId> <WarnReason> (Warn by ID)\nAdminForceTeamChangeById <PlayerId> (Switch by ID)\nAdminBroadcast <Message> (Broadcast)\nAdminSetNextLayer <LayerName> [<FactionSetup1>] [<FactionSetup2>] (Next layer)\nAdminChangeLayer <LayerName> (Change layer)\nAdminClearNextLayer (Clear next)\nAdminEndMatch (End)\nAdminRestartMatch (Restart)\nAdminReloadServerConfig (Reload)\nAdminPauseMatch (Console only)";
        public PoolView Pool=new(new("test","192.0.2.1","ready",null,"Owner"),[new("test-instance","owner","Owner","Server",0,PortSet.ForSlot(0),"running",true,null,"gameReady",null,100,"",null,null,"")],false,false,null);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            object value=new{output=""};
            if(request.RequestUri!.AbsolutePath.EndsWith("/rcon"))
            {
                var command=(await request.Content!.ReadFromJsonAsync<CommandRequest>(cancellationToken:ct))!.Command;Calls.Add(command);
                value=new{output=command=="ListCommands 1"?Catalog:command=="ListPlayers"?"----- Active Players -----\nID: 99 | Online IDs: steam: 76561198000000001 | Name: Renamed | Team ID: 2 | Squad ID: 9 | Is Leader: False\n----- Recently Disconnected Players -----":Reply};
            }
            else if(request.RequestUri.AbsolutePath.EndsWith("/pool"))value=Pool;
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}),Encoding.UTF8,"application/json")};
        }
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T value)yield return value;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Children<T>(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
    public static async Task Run(MainWindow window)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;var replies=new Replies();
        void Set(string name,object value)=>typeof(MainWindow).GetField(name,flags)!.SetValue(window,value);
        T Get<T>(string name)=>(T)window.FindName(name);
        void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
        async Task Invoke(string name,params object[] args){var result=typeof(MainWindow).GetMethod(name,flags)!.Invoke(window,args);if(result is Task task)await task;}
        ((DispatcherTimer)typeof(MainWindow).GetField("timer",flags)!.GetValue(window)!).Stop();
        Set("http",new HttpClient(replies){BaseAddress=new("https://mock.test/")});Set("session",new SessionResponse("test","owner","Owner",true));Set("pool",replies.Pool);Set("selectedId","test-instance");Set("allowClose",true);
        Get<TabControl>("Tabs").SelectedIndex=3;await Task.Delay(150);
        var confirm=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
        confirm.Tick+=(_,_)=>
        {
            foreach(Window dialog in Application.Current.Windows.Cast<Window>().Where(w=>w.Owner==window).ToArray())
            {
                var yes=Children<Button>(dialog).FirstOrDefault(b=>b.Content?.ToString()==SquadHostManager.Desktop.Localization.T("dialogYes"));
                yes?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        };
        confirm.Start();
        try
        {
            async Task Click(string action)
            {
                await Invoke("RenderPlayers",new List<SquadPlayer>{new("3","Original","1","2","76561198000000001",true)},true);
                Get<DataGrid>("PlayerList").SelectedIndex=0;Set("playersLoadedId","test-instance");Get<TextBox>("PlayerReason").Text="Rule violation";Get<TextBox>("BanDuration").Text="1m";
                replies.Calls.Clear();var button=Children<Button>(Get<StackPanel>("PlayersPanel")).Single(b=>b.Tag?.ToString()==action);
                await Invoke("PlayerActionClick",button,new RoutedEventArgs());
                for(var i=0;i<100 && (bool)typeof(MainWindow).GetField("busy",flags)!.GetValue(window)!;i++)await Task.Delay(20);
                Assert(!(bool)typeof(MainWindow).GetField("busy",flags)!.GetValue(window)!,"Player action did not finish");
            }
            foreach(var action in new[]{"AdminKick","AdminBan","AdminWarn","AdminForceTeamChange"})
            {
                replies.Reply="Command applied.";await Click(action);
                Assert(replies.Calls[0].StartsWith(action+" \"76561198000000001\""),"Player action used temporary ID or stale roster guard");
                Assert(replies.Calls.Count(c=>c.StartsWith("Admin"))==1,"Player action was replayed");
                Assert(Get<TextBlock>("PlayerActionResult").Text==replies.Reply,"Player action response is hidden");
            }
            replies.Reply="Unable to find player";await Click("AdminKick");Assert(Get<TextBlock>("PlayerActionResult").Text==replies.Reply && Get<FrameworkElement>("ErrorPanel").Visibility==Visibility.Visible,"Server rejection is hidden");
            replies.Reply="";await Click("AdminKick");Assert(Get<TextBlock>("PlayerActionResult").Text==SquadHostManager.Desktop.Localization.T("rconNoConfirmation"),"Empty reply falsely reports success");
            Console.WriteLine("PASS: real player buttons use SteamID; roster changes do not block actions; rejection and empty responses are visible; no command replay");
            replies.Reply="Command applied.";
            await Invoke("LoadMatchCommands");var picker=Get<ComboBox>("MatchCommandPicker");
            string Name(object item)=>(string)item.GetType().GetProperty("Name")!.GetValue(item)!;
            var catalog=picker.Items.Cast<object>().ToArray();Assert(catalog.Length==20 && catalog.All(i=>Name(i)!="AdminPauseMatch"),"RCON catalog contains unsupported commands or omits supported actions");
            foreach(var item in catalog)
            {
                picker.SelectedItem=item;var usage=(string)item.GetType().GetProperty("Usage")!.GetValue(item)!;
                var parameters=RconCommands.Parameters(usage);var fields=Get<WrapPanel>("MatchParameters").Children.Cast<StackPanel>().Select(p=>p.Children.OfType<TextBox>().Single()).ToArray();
                for(var i=0;i<fields.Length;i++)fields[i].Text=parameters[i].Name switch{"PlayerId"=>"3","NameOrSteamId" or "NameOrEOSId"=>"76561198000000001","BanLength"=>"1m","LayerName"=>"AlBasrah_AAS_v1","FactionSetup1" or "FactionSetup2"=>"",_=>"Reason with spaces"};
                replies.Calls.Clear();await Invoke("RunMatchCommandClick",window,new RoutedEventArgs());
                for(var i=0;i<100 && (bool)typeof(MainWindow).GetField("busy",flags)!.GetValue(window)!;i++)await Task.Delay(20);
                Assert(replies.Calls.Count==1 && replies.Calls[0].StartsWith(Name(item)),"Catalog command was not sent exactly once: "+Name(item));
                Assert(!replies.Calls[0].Contains('[') && !replies.Calls[0].Contains('<'),"Template placeholders reached RCON");
                Assert(Get<FrameworkElement>("ErrorPanel").Visibility!=Visibility.Visible,"Valid catalog command failed: "+Name(item));
            }
            Console.WriteLine("PASS: all 20 curated RCON commands through the real command form, quoting, optional parameters and one send per action");
        }
        finally{confirm.Stop();}
    }
}
