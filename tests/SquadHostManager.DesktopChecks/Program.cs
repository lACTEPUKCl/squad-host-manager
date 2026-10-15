using System.Net.Http.Json;
using Localization=SquadHostManager.Desktop.Localization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;
using SquadHostManager.Desktop;
class QA {
class Mock:HttpMessageHandler {
 public int DelayWrites; public bool FailWrites,FailReload;
 public string Admins="Group=Moderator:kick\n";
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){object value=new{output=""};
 if(r.RequestUri!.AbsolutePath.EndsWith("/server-defaults/config")){var request=(await r.Content!.ReadFromJsonAsync<ConfigRequest>(cancellationToken:ct))!;if(request.Content!=null){if(FailWrites)throw new HttpRequestException("Offline");if(DelayWrites>0)await Task.Delay(DelayWrites,ct);Admins=request.Content;}value=new{output=request.File=="Admins.cfg"?Admins:StandardConfigs.Get(request.File),failed=FailReload?new[]{"Test server"}:Array.Empty<string>()};}
 else if(r.RequestUri.AbsolutePath.EndsWith("/versions"))value=Array.Empty<ConfigRevision>();
 return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}),Encoding.UTF8,"application/json")};}
}
[STAThread]static void Main(){var app=new Application();app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/SquadHostManager;component/DashboardTheme.xaml",UriKind.Relative)});Localization.Initialize();
var window=new MainWindow(false){Left=-10000,Top=-10000,ShowInTaskbar=false};var flags=BindingFlags.Instance|BindingFlags.NonPublic;var mock=new Mock();
void Set(string n,object v)=>typeof(MainWindow).GetField(n,flags)!.SetValue(window,v);
ConfigEditor Editor()=>(ConfigEditor)typeof(MainWindow).GetField("configurationEditor",flags)!.GetValue(window)!;
T Field<T>(string n)=>(T)typeof(ConfigEditor).GetField(n,flags)!.GetValue(Editor())!;
void Assert(bool ok,string m){if(!ok)throw new Exception(m);}
app.Startup+=async(_,_)=>{try{window.Show();Set("http",new HttpClient(mock){BaseAddress=new("https://mock.test/")});Set("session",new SessionResponse("mock",Guid.NewGuid().ToString(),"Owner",true));
((FrameworkElement)window.FindName("LoginPanel")).Visibility=Visibility.Collapsed;((FrameworkElement)window.FindName("Workspace")).Visibility=Visibility.Visible;
var tabs=(TabControl)window.FindName("Tabs");var admins=(TabItem)window.FindName("LegacyAdminsTab");Assert(admins.Visibility==Visibility.Visible,"Admin tab hidden");tabs.SelectedItem=admins;await Task.Delay(250);
Assert(Field<ListBox>("navigation").Visibility==Visibility.Collapsed,"Extra file list in admins");Assert(Field<TextBox>("raw").Text.Contains("Moderator"),"Shared admins not loaded");
Field<TextBox>("raw").Text="Group=Operators:kick,ban\nAdmin=76561198000000001:Operators";await Editor().Flush();Assert(mock.Admins.Contains("Operators"),"Shared admins not saved");
tabs.SelectedItem=window.FindName("ConfigurationTab");await Task.Delay(250);Field<ListBox>("navigation").SelectedIndex=1;await Task.Delay(250);Assert(Field<TextBox>("raw").Text==mock.Admins,"Config tab has different admins");
tabs.SelectedItem=admins;await Task.Delay(250);Assert(Field<TextBox>("raw").Text==mock.Admins,"Returning loses admins");mock.DelayWrites=300;Field<TextBox>("raw").Text+="\nGroup=Race:kick";
tabs.SelectedItem=window.FindName("ConfigurationTab");await Task.Delay(20);tabs.SelectedItem=admins;await Task.Delay(1200);
Assert((bool)typeof(MainWindow).GetField("configurationForAdmins",flags)!.GetValue(window)!,"Rapid tab switching opened the wrong editor");
Assert(Field<TextBox>("raw").Text.Contains("Race"),"Rapid tab switching lost pending edits");
mock.DelayWrites=0;mock.FailWrites=true;Field<TextBox>("raw").Text+="\nGroup=Offline:ban";await Editor().Flush();
tabs.SelectedItem=window.FindName("ConfigurationTab");await Task.Delay(250);Field<ListBox>("navigation").SelectedIndex=1;await Task.Delay(250);
Assert(Field<TextBox>("raw").Text.Contains("Offline"),"Offline draft lost on tab change");mock.FailWrites=false;mock.FailReload=true;await Editor().Flush();Assert(Field<TextBlock>("status").Text.Contains("Test server"),"Failed reload not shown");mock.FailReload=false;await Editor().Flush();Assert(mock.Admins.Contains("Offline"),"Offline draft not retried");
tabs.SelectedItem=admins;await Task.Delay(250);Assert(Field<TextBox>("raw").Text.Contains("Offline"),"Retried draft missing from admins");
Console.WriteLine("PASS: rapid switching, offline drafts and RCON failure recovery;  visible admins tab, shared save, config switch, return without data loss");await RconChecks.Run(window);app.Shutdown();}catch(Exception e){Console.WriteLine(e);Environment.ExitCode=1;app.Shutdown();}};app.Run();}}

