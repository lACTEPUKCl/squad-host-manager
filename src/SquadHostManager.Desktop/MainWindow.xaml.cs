using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.AspNetCore.SignalR.Client;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty NoServerHelpVisibilityProperty=DependencyProperty.Register(nameof(NoServerHelpVisibility),typeof(Visibility),typeof(MainWindow),new PropertyMetadata(Visibility.Collapsed));
    public Visibility NoServerHelpVisibility{get=>(Visibility)GetValue(NoServerHelpVisibilityProperty);set=>SetValue(NoServerHelpVisibilityProperty,value);}
    public ICommand ServerSetupShortcut=>new SetupShortcut(this);
    private sealed class SetupShortcut(MainWindow window):ICommand
    {
        public bool CanExecute(object? parameter)=>true;
        public void Execute(object? parameter)=>window.Tabs.SelectedItem=window.MyServersTab;
        public event EventHandler? CanExecuteChanged{add{}remove{}}
    }
    private HttpClient? http;
    private HubConnection? hub;
    private SessionResponse? session;
    private PoolView? pool;
    private bool allowClose, busy, refreshing;
    private string? displayedInstance;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private static readonly string Settings = SessionSettingsPath();
    private static string SessionSettingsPath()
    {
        var args=Environment.GetCommandLineArgs();var index=Array.IndexOf(args,"--profile");
        var profile=index>=0 && index+1<args.Length?args[index+1]:null;
        if(profile!=null && !System.Text.RegularExpressions.Regex.IsMatch(profile,@"^[a-zA-Z0-9_-]{1,40}$"))throw new ArgumentException("Invalid profile name.");
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SquadHostManager",profile==null?"session.dat":"session-"+profile+".dat");
    }
    private sealed record Saved(string Endpoint, SessionResponse Session,string? Invitation=null,string? CertificateSha256=null);
    private string? rememberedInvitation;
    private string? certificateSha256;
    private sealed record ServerRow(InstanceView Value)
    {
        public string PasswordLabel=>string.IsNullOrEmpty(Value.Password)?Localization.T("serverNoPassword"):Localization.T("serverPasswordLabel")+": "+Value.Password;
        public string DotColor=>Value.GameState=="gameReady"?"#B2FF59":Value.Phase=="error" || Value.GameState=="gameFailed"?"#E6A16B":"#A3AC9A";
        public string Name => Value.Name; public string OwnerName => Value.OwnerName; public PortSet Ports => Value.Ports;
        public string State => Localization.T(Value.Phase == "running" ? Value.GameState ?? "gameStarting" : Value.Phase);
    }
    private sealed record UserRow(UserAccessView Value)
    {
        public string Name => Value.Name; public string State => Localization.T(Value.Disabled ? "disabled" : Value.ExpiresAt<DateTimeOffset.UtcNow?"accessExpired":Value.HasKey ? "active" : "passwordAccess")+(Value.IsAdmin?"":" · "+Value.MaxServers+" · "+(Value.ExpiresAt?.ToLocalTime().AddTicks(-1).ToString("d")??"∞"));
    }
    private static string L(string key, params object?[] args) => Localization.T(key, args);
    private sealed class InputException(string message) : Exception(message);
    private sealed class RequestException(string message, string details,System.Net.HttpStatusCode? code=null) : Exception(details)
    { public string UserMessage { get; } = message;public System.Net.HttpStatusCode? Code{get;}=code; }
    public MainWindow() : this(!Environment.GetCommandLineArgs().Contains("--no-session")) { }
    public MainWindow(bool restoreSession)
    {
        Localization.Initialize(); InitializeComponent();Width=Math.Min(1640,SystemParameters.WorkArea.Width*0.94);Height=Math.Min(1040,SystemParameters.WorkArea.Height*0.94);Tabs.SizeChanged+=(_,e)=>{var compact=e.NewSize.Height<900;if(CompactSidebar!=compact){CompactSidebar=compact;if(Tabs.Template.FindName("NavigationScroll",Tabs) is ScrollViewer navigation)navigation.ScrollToTop();}};InitializeDraftSaving();BuildFeatures();RefreshSharedProfiles(); InitializePermissions(); RenderPlayers([]); RefreshColumnHeaders();Closed+=(_,_)=>availabilityTray?.Dispose();
        VersionLabel.Text = L("developmentVersion");
        AboutVersion.Text="Squad Server Manager · "+System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(System.Reflection.Assembly.GetExecutingAssembly())?.InformationalVersion.Split('+')[0];
        if(restoreSession)ContentRendered+=async(_,_)=>{if(startupUpdatesChecked)return;startupUpdatesChecked=true;await CheckAppUpdates(true);};
        StatisticsFrom.SelectedDate=DateTime.Today.AddDays(-7);
        LanguagePicker.SelectedIndex = Array.IndexOf(Localization.Languages, Localization.Language);
        timer.Tick += async (_, _) => { if (!busy) await BackgroundRefresh(true); }; timer.Start();
        if (restoreSession) Loaded += async (_, _) =>
        {
            try
            {
                if (!System.IO.File.Exists(Settings)) return;
                var saved = JsonSerializer.Deserialize<Saved>(ProtectedData.Unprotect(System.IO.File.ReadAllBytes(Settings), null, DataProtectionScope.CurrentUser));
                if (saved == null) return;
                if(saved.Endpoint==LocalCoordinator.Endpoint && LocalCoordinator.Configured)await LocalCoordinator.EnsureStarted();
                Endpoint.Text = saved.Endpoint;certificateSha256=saved.CertificateSha256; Username.Text = saved.Session.Name;
                var owner=SharedHostingProfiles.Read().FirstOrDefault(p=>p.Endpoint==saved.Endpoint);
                certificateSha256=saved.CertificateSha256??owner?.CertificateSha256;rememberedInvitation=saved.Invitation;
                await Safe(() => Connect(saved.Session));
            }
            catch { Status.Text = L("savedInvalid"); }
        };
    }
    private void MinimizeWindowClick(object sender,RoutedEventArgs e)=>SystemCommands.MinimizeWindow(this);
    private void MaximizeWindowClick(object sender,RoutedEventArgs e){if(WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(this);else SystemCommands.MaximizeWindow(this);}
    private void CloseWindowClick(object sender,RoutedEventArgs e)=>SystemCommands.CloseWindow(this);
    private Uri BaseUri()
    {
        if (!Uri.TryCreate(Endpoint.Text.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.UserInfo!="" || uri.Query!="" || uri.Fragment!="" || (!uri.IsLoopback && uri.Scheme != "https") || (uri.Scheme != "https" && uri.Scheme != "http")) throw new InputException(L("httpsRequired"));
        return uri;
    }
    private async Task<T> Post<T>(string path, object body, HttpClient? client = null)
    {
        using var response = await (client ?? http ?? throw new InputException(L("signInFirst"))).PostAsJsonAsync(path, body);
        await Check(response); return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static async Task Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync();
        try { using var doc = JsonDocument.Parse(text); text = doc.RootElement.GetProperty("error").GetString() ?? text; } catch { text = $"HTTP {(int)response.StatusCode}"; }
        var key = response.StatusCode switch { System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "accessDenied", System.Net.HttpStatusCode.BadRequest => "invalidInput", System.Net.HttpStatusCode.Conflict => "unavailableAction", _ => "actionFailed" };
        throw new RequestException(L(key), text,response.StatusCode);
    }
    private async Task Safe(Func<Task> action,bool showActivity=true)
    {
        if (busy) return; busy = true; LoginPanel.IsEnabled=false;LoginFeedback.Text=""; OwnServers.IsEnabled = AddServerButton.IsEnabled = false; Activity.Visibility = showActivity?Visibility.Visible:Visibility.Hidden; ErrorPanel.Visibility = Visibility.Collapsed;
        try { await action(); }
        catch (Exception e) { Status.Text = e is InputException ? e.Message : e is RequestException request ? request.UserMessage : e is HttpRequestException or TaskCanceledException ? L("networkLost") : L("actionFailed");if(LoginPanel.Visibility==Visibility.Visible)LoginFeedback.Text=Status.Text; ErrorDetails.Text = e.Message; ErrorPanel.Visibility = Visibility.Visible; }
        finally { busy = false;LoginPanel.IsEnabled=true; OwnServers.IsEnabled = AddServerButton.IsEnabled = true; Activity.Visibility = Visibility.Hidden; if(pendingSnapshot && http!=null) _ = Dispatcher.BeginInvoke(new Action(async()=>await BackgroundRefresh())); }
    }
    private async Task Connect(SessionResponse value)
    {
        draftSaveTimer.Stop();draftPersistenceReady=false;
        if (hub != null) await hub.DisposeAsync(); http?.Dispose(); session = value;
        var uri = BaseUri();var pin=certificateSha256; http = HostingTransport.Client(uri,pin);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", value.Token); await Refresh();using(var defaultsResponse=await http.GetAsync("api/server-defaults")){if(defaultsResponse.IsSuccessStatusCode){var savedDefaults=await defaultsResponse.Content.ReadFromJsonAsync<ServerDefaults>();defaultMods=savedDefaults?.Mods??"";if(Mine==null)Mods.Text=defaultMods;else if(savedDefaults?.Admins==null && pool?.Host.Phase=="ready" && Mine.Phase is "running" or "stopped" or "error"){using var currentAdmins=await http.PostAsJsonAsync($"api/instances/{Mine.Id}/config",new ConfigRequest("Admins.cfg"));}}}using(var configResponse=await http.PostAsJsonAsync("api/server-defaults/config",new ConfigRequest("Server.cfg"))){if(configResponse.IsSuccessStatusCode && Mine==null){var config=(await configResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("output").GetString()??"";MaxPlayers.Text=SquadConfig.Get(config,"MaxPlayers")??MaxPlayers.Text;}}RestoreServerDraft();
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Settings)!);
        System.IO.File.WriteAllBytes(Settings, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new Saved(uri.ToString(), value,rememberedInvitation,pin)), null, DataProtectionScope.CurrentUser));
        hub = new HubConnectionBuilder().WithUrl(new Uri(uri, "events"), o =>
        {
            o.AccessTokenProvider = () => Task.FromResult(session?.Token);
            o.HttpMessageHandlerFactory=_=>HostingTransport.Handler(uri,pin);
            if(pin!=null)o.WebSocketConfiguration=socket=>socket.RemoteCertificateValidationCallback=(_,certificate,_,errors)=>HostingTransport.AcceptCertificate(certificate as System.Security.Cryptography.X509Certificates.X509Certificate2,errors,pin);
        }).WithAutomaticReconnect().Build();
        hub.On("changed", () => Dispatcher.InvokeAsync(async () => await BackgroundRefresh()));
        hub.Reconnected += _ => Dispatcher.InvokeAsync(async () => await Safe(Refresh)).Task;
        hub.Reconnecting += _ => Dispatcher.InvokeAsync(() => Status.Text = L("polling")).Task;
        try { await hub.StartAsync(); } catch { Status.Text = L("polling"); }
        LoginPanel.Visibility = Visibility.Collapsed; Workspace.Visibility = Visibility.Visible;
        StatisticsTab.Visibility = OwnerSettings.Visibility = value.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        AdminPanel.Visibility=value.IsAdmin && !PersonalHosting?Visibility.Visible:Visibility.Collapsed;
        HostingModeHelp.Text=L(PersonalHosting?"personalModeHelp":session?.IsAdmin==true?"sharedOwnerModeHelp":"sharedModeHelp");PersonalAccountButton.Visibility=PersonalHosting?Visibility.Visible:Visibility.Collapsed;
        RemoveSharedHostingButton.Visibility=value.IsAdmin && SharedHostingProfiles.Read().Any(p=>p.Endpoint==uri.ToString())?Visibility.Visible:Visibility.Collapsed;
        AccountLabel.Text = value.Name; AccountPassword.Clear(); AccessKey.Clear();
        if (value.IsAdmin && !PersonalHosting) await RefreshUsers();
        try{var health=await http.GetFromJsonAsync<JsonElement>("healthz");featureApiAvailable=health.TryGetProperty("features",out var available) && available.GetBoolean();if(featureApiAvailable)await LoadFeatures();await LoadBalance(true);}catch{featureApiAvailable=false;}
        if (pool?.Host.Id==null && pool?.Host.Rental?.EndedAt!=null) Tabs.SelectedItem=SharedHostTab;
        RestoreLocationWatches();
        try { if(pool?.Host.Id==null || locationWatch.Selected.Count>0) await LoadLocations(); } catch (Exception e) { ErrorDetails.Text=e.Message; ErrorPanel.Visibility=Visibility.Visible; }
    }
    private async Task Refresh()
    {
        if (http == null || refreshing) return; refreshing = true;
        try
        {
            using var response = await http.GetAsync("api/pool");
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            { await DisconnectLocal(); throw new InputException(L("sessionExpired")); }
            await Check(response); pool = (await response.Content.ReadFromJsonAsync<PoolView>())!; RenderPool();
        }
        catch(RequestException e) when(e.Code==System.Net.HttpStatusCode.TooManyRequests){throw;}
        catch { DisableActions(); HostStatus.Text = L("hostConnectionLost"); throw; }
        finally { refreshing = false; }
    }
    private void RenderPool()
    {
        if (pool == null) return;
        if(Tabs.SelectedItem is TabItem page)PageHeading.Text=page.Header?.ToString()??"";
        timer.Interval=TimeSpan.FromSeconds(pool.Busy && hub?.State!=HubConnectionState.Connected?5:15);
        StatisticsTab.Visibility=session?.IsAdmin==true?Visibility.Visible:Visibility.Collapsed;
        Servers.Visibility = session?.IsAdmin==true && pool.Instances.Any(i=>i.Phase!="stopped")?Visibility.Visible:Visibility.Collapsed;
        var serverRows=pool.Instances.Where(i=>i.Phase!="stopped").Select(i=>new ServerRow(i)).ToList();
        if(Servers.ItemsSource is not List<ServerRow> previousRows || !previousRows.SequenceEqual(serverRows))Servers.ItemsSource=serverRows;

        RenterStatus.Text = pool.Host.RentedByName is { } renter ? L("rentedBy", renter) : pool.Host.ConnectedByName is { } connected ? L("connectedBy", connected) : L("unknownRenter");
        RenderRental();
        RenderSelection();
        OverviewLocation.Text=pool.Host.Id==null?"":pool.Host.Rental?.Location??"";
        OverviewAddress.Text=pool.Host.Ip??L("hostNotRented");
        OverviewRenter.Text=pool.Host.Id==null?"":RenterStatus.Text;
        OverviewRental.Visibility=pool.Host.Id==null?Visibility.Collapsed:Visibility.Visible;
        var rental=pool.Host.Rental;OverviewHourly.Text=rental?.HourlyRate is {} hourly?hourly.ToString("0.000")+" "+rental.Currency:"—";
        var overview=pool.Instances.Where(i=>i.IsMine && i.Phase!="stopped").Select(i=>new ServerRow(i)).ToList();
        if(OverviewServers.ItemsSource is not List<ServerRow> prior || !prior.SequenceEqual(overview))OverviewServers.ItemsSource=overview;
        OverviewEmpty.Visibility=overview.Count==0?Visibility.Visible:Visibility.Collapsed;OverviewCount.Text=overview.Count.ToString();
        var mine = Mine; StartButton.IsEnabled = (mine == null || mine.Phase == "stopped") && pool.CanStart && pool.Host.Id != null; StopButton.IsEnabled = mine != null && mine.Phase is not ("stopped" or "stopping");
        ServerSetupHelp.Visibility=pool.Host.Id==null?Visibility.Visible:Visibility.Collapsed;
        InstallationStepsHelp.Visibility=GameReadinessHint.Visibility=mine==null?Visibility.Collapsed:Visibility.Visible;
        RestartButton.Visibility=mine?.Phase is "running" or "error"?Visibility.Visible:Visibility.Collapsed;
        SaveSettingsButton.Visibility=mine==null?Visibility.Collapsed:Visibility.Visible;
        StopButton.Visibility=mine==null || mine.Phase=="stopped"?Visibility.Collapsed:Visibility.Visible;
        StartButton.Visibility=mine==null || mine.Phase=="stopped"?Visibility.Visible:Visibility.Collapsed;
        ReleaseEmptyHostButton.Visibility=pool.CanRelease?Visibility.Visible:Visibility.Collapsed;
        SaveModsButton.IsEnabled = SaveSettingsButton.IsEnabled = mine?.Phase is "running" or "stopped" or "error";
        ServerName.IsEnabled = ServerPassword.IsEnabled = MaxPlayers.IsEnabled = Mods.IsEnabled = mine == null || mine.Phase is "running" or "stopped" or "error";
        if (mine != null && displayedInstance != mine.Id) { ServerName.Text = mine.Name; MaxPlayers.Text = mine.MaxPlayers.ToString(); Mods.Text = mine.Mods; ServerPassword.Password = mine.Password ?? ""; displayedInstance = mine.Id; UpdateModList(); }
        if (mine == null) displayedInstance = null;
        ReleaseButton.IsEnabled = pool.CanRelease; ReleaseButton.Visibility = pool.CanRelease ? Visibility.Visible : Visibility.Collapsed;
        ConfigButton.IsEnabled = ArchiveButton.IsEnabled = LogsButton.IsEnabled = mine?.Phase is "running" or "stopped" or "error";
        FilesRefreshButton.IsEnabled = DownloadFileButton.IsEnabled = ConfigButton.IsEnabled;
        LogsButton.IsEnabled = mine?.Phase is "running" or "starting" or "restarting" or "error";
        MatchRunButton.IsEnabled=MatchCommandPicker.SelectedItem is MatchCommand && mine?.GameState=="gameReady";
        CommandButton.IsEnabled = mine?.Phase == "running" && mine.GameState == "gameReady";
        RefreshPlayersButton.IsEnabled = PlayersPanel.IsEnabled = MatchPanel.IsEnabled = CommandButton.IsEnabled;
        if (PlayerList.Items.Count == 0) PlayerCount.Text = mine?.GameState == "gameReady" ? L("playersCount", 0) : L(mine?.GameState ?? "startFirst");
        RestartButton.IsEnabled = mine?.Phase is "running" or "error";
        RestartMatchButton.IsEnabled = EndMatchButton.IsEnabled = CommandButton.IsEnabled;
        MineStatus.Text = mine == null ? L(draft ? "newServer" : pool.Host.Id==null ? pool.Host.Rental?.EndedAt!=null ? "hostCancelled" : "hostNotRented" : "noServers") : mine.Name + "\n" + L(mine.Phase == "queued" && pool.Host.Phase != "ready" ? pool.Host.Phase : mine.Phase == "running" ? mine.GameState ?? "gameStarting" : mine.Phase) + (mine.DownloadPercent is { } percent ? " " + percent.ToString("0.0") + "%" : "");
        MineAddress.Text = mine == null || pool.Host.Ip == null ? "" : $"{pool.Host.Ip}:{mine.Ports.Game}\nSteam {mine.Ports.Game + 1}   Query {mine.Ports.Query}   RCON {mine.Ports.Rcon}   Beacon {mine.Ports.Beacon}";
        RenderConfiguredMods();
        _ = FetchConfiguredModNames();
        RenderTiming(mine);
        OwnError.Text = mine?.Phase != "error" || mine.Error == null ? "" : L("actionFailed");
        Status.Text = hub?.State==Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected?L("connected",session?.Name):L("polling");
        var error = (pool.Busy && pool.Host.Startup?.Stage is not (null or "hostFailed")?null:pool.Host.Error) ?? (mine?.Phase=="error"?mine.Error:null);
        if (pool.Host.Id == null && !pool.CanStart && pool.Host.Phase == "empty") { Status.Text=L("rentPaused"); ErrorPanel.Visibility=Visibility.Collapsed; }
        else if (error != null) { displayedPoolError=error;ErrorDetails.Text = error; ErrorPanel.Visibility = Visibility.Visible; Status.Text = L("actionFailed"); }
        else if(displayedPoolError!=null){if(ErrorDetails.Text==displayedPoolError){ErrorDetails.Clear();ErrorPanel.Visibility=Visibility.Collapsed;}displayedPoolError=null;}
    }
    private string? displayedPoolError;
    private void DisableActions() { RefreshPlayersButton.IsEnabled = PlayersPanel.IsEnabled = FilesRefreshButton.IsEnabled = DownloadFileButton.IsEnabled = SaveSettingsButton.IsEnabled = RestartMatchButton.IsEnabled = EndMatchButton.IsEnabled = RestartButton.IsEnabled = StartButton.IsEnabled = StopButton.IsEnabled = ReleaseButton.IsEnabled = ConfigButton.IsEnabled = ArchiveButton.IsEnabled = LogsButton.IsEnabled = CommandButton.IsEnabled = false; }
    private InstanceView? Mine => pool?.Instances.FirstOrDefault(i => i.IsMine && i.Id == selectedId);
    private InstanceView RequiredMine() => Mine ?? throw new InputException(L("startFirst"));
    private async Task Start()
    {
        if (!int.TryParse(MaxPlayers.Text, out var max) || max is < 2 or > 100) throw new InputException(L("invalidPlayers"));
        var startingDraft=Mine==null;
        await Post<OperationView>("api/instances", new StartRequest(Guid.NewGuid(), ServerName.Text, ServerPassword.Password, Mods.Text, max, Mine?.Id,Mine?.StartLayer??""));defaultMods=Mods.Text;if(startingDraft)ClearServerDraft(); draft = false; selectedId = null; await Refresh();
    }
    private async Task Stop(bool release) { await Post<OperationView>("api/stop", new StopRequest(Guid.NewGuid(), release, release && pool?.CanRelease == true ? null : RequiredMine().Id)); await Refresh(); }
    private bool Confirm(string message, string title) => AppDialog.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    private static void EnsureRconReply(string reply){if(RconCommands.Rejected(reply))throw new InputException(reply);}
    private async Task Rcon(string command) { try{RconCommands.Validate(command);}catch(ArgumentException){throw new InputException(L("rconInvalidParameters"));}var mine = RequiredMine(); var result = await Post<JsonElement>($"api/instances/{mine.Id}/rcon", new CommandRequest(command));var reply=result.GetProperty("output").GetString()??""; if (Mine?.Id == mine.Id) { Output.Text = reply.Length==0?L("rconNoConfirmation"):reply; ConsoleViews.SelectedIndex = 1; Status.Text=reply.Length==0?L("rconNoConfirmation"):reply.Split('\n')[0]; } EnsureRconReply(reply); }
    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private bool PersonalHosting => Uri.TryCreate(Endpoint.Text,UriKind.Absolute,out var address) && address.IsLoopback;
    private void OwnerLoginExpanded(object sender,RoutedEventArgs e){InvitationEntry.Visibility=Visibility.Collapsed;LoginFeedback.Text="";ErrorPanel.Visibility=Visibility.Collapsed;}
    private void OwnerLoginCollapsed(object sender,RoutedEventArgs e)=>InvitationEntry.Visibility=Visibility.Visible;
    private void AccessKeyKeyDown(object sender, KeyEventArgs e) { if(e.Key==Key.Enter){e.Handled=true;KeyLoginClick(sender,e);} }
    private void GoHostingClick(object sender,RoutedEventArgs e)=>Tabs.SelectedItem=SharedHostTab;
    private async void KeyLoginClick(object sender, RoutedEventArgs e) => await Safe(()=>SignInWithInvitation(AccessKey.Password.Trim()));
    private async Task SignInWithInvitation(string original)
    {
        var key=original;ConnectionInvite? invite;
        if(key.Length==0)throw new InputException(L("invitationEmpty"));
        if(key.Length>4096 || !key.StartsWith("SHM1.",StringComparison.Ordinal) && key.Length>512)throw new InputException(L("invitationInvalid"));
        try{invite=ConnectionInvite.Decode(key);}catch(ArgumentException){throw new InputException(L("invitationInvalid"));}
        if(invite!=null && new Uri(invite.Endpoint).IsLoopback)throw new InputException(L("invitationLocal"));
        if(invite!=null){Endpoint.Text=invite.Endpoint;certificateSha256=invite.CertificateSha256;key=invite.Key;}
        else if(PersonalHosting)throw new InputException(L("invitationInvalid"));
        else certificateSha256=null;
        using var client = HostingTransport.Client(BaseUri(),certificateSha256,TimeSpan.FromSeconds(30));SessionResponse login;
        try{login=await Post<SessionResponse>("api/key-login",new KeyLoginRequest(key),client);}
        catch(RequestException error) when(error.Code is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden){throw new InputException(L("invitationRejected"));}
        rememberedInvitation=original;await Connect(login);
    }
    private async void LoginClick(object sender, RoutedEventArgs e) => await Safe(async () => { if(string.IsNullOrWhiteSpace(Username.Text)||AccountPassword.Password.Length==0)throw new InputException(L("loginFieldsRequired"));certificateSha256=SharedHostingProfiles.Read().FirstOrDefault(p=>p.Endpoint==BaseUri().ToString())?.CertificateSha256;using var client = HostingTransport.Client(BaseUri(),certificateSha256);var login=await Post<SessionResponse>("api/login", new LoginRequest(Username.Text, AccountPassword.Password), client);rememberedInvitation=null;await Connect(login);if(login.IsAdmin && !PersonalHosting){SharedHostingProfiles.Save(new(BaseUri().ToString(),certificateSha256,AccountPassword.Password,"",BaseUri().Host));RefreshSharedProfiles();} });
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Safe(async () => { using var response=await http!.GetAsync("api/pool?refresh=true"); await Check(response); pool=(await response.Content.ReadFromJsonAsync<PoolView>())!; RenderPool(); });
    private async void StartClick(object sender, RoutedEventArgs e) => await Safe(Start);
    private async void StopClick(object sender, RoutedEventArgs e) => await Safe(async () => {
        await Refresh(); var mine=RequiredMine();
        var others=(pool!.OtherActiveServers??pool.Instances.Count(i=>!i.IsMine && i.Phase is not ("stopped" or "stopping")))>0 || pool.Instances.Any(i=>i.Id!=mine.Id && i.IsMine && i.Phase is not ("stopped" or "stopping"));
        var ending=(pool.OtherStoppingServers??pool.Instances.Count(i=>!i.IsMine && i.Phase=="stopping"))>0 || pool.Instances.Any(i=>i.Id!=mine.Id && i.IsMine && i.Phase=="stopping");
        var release=!others && pool.ReleaseEnabled;
        var message=others?L("stopSharedContinues"):release?L(ending?"stopAllEnding":"stopLastEnds"):L("stopRentalKept");
        if(Confirm(message,L("stop")))await Stop(pool.ReleaseEnabled);
    });
    private async void RestartClick(object sender, RoutedEventArgs e) => await Safe(async () => { if (!Confirm(L("restartServerConfirm"), L("restartServer"))) return; var mine=RequiredMine();
        if(Mods.Text!=mine.Mods) await Post<System.Text.Json.JsonElement>($"api/instances/{mine.Id}/settings",new ServerSettingsRequest(mine.Name,null,Mods.Text,mine.MaxPlayers));defaultMods=Mods.Text;
        await Post<OperationView>($"api/instances/{mine.Id}/restart", new RestartRequest(Guid.NewGuid())); await Refresh(); });
    private async void ReleaseClick(object sender, RoutedEventArgs e) => await Safe(async () => { if (Confirm(L("releaseConfirm"), L("release"))) await Stop(true); });
    private async void PlayersClick(object sender, RoutedEventArgs e) => await Safe(() => Rcon("ListPlayers"));
    private async void CommandClick(object sender, RoutedEventArgs e) => await Safe(() => Rcon(Command.Text.Trim()));
    private async void CommandKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await Safe(() => Rcon(Command.Text.Trim())); } }
    private async void RestartMatchClick(object sender, RoutedEventArgs e) => await Safe(async () => { if (Confirm(L("restartConfirm"), L("match"))) await Rcon("AdminRestartMatch"); });
    private async void EndMatchClick(object sender, RoutedEventArgs e) => await Safe(async () => { if (Confirm(L("endConfirm"), L("match"))) await Rcon("AdminEndMatch"); });
    private async void LogsClick(object sender, RoutedEventArgs e) => await Safe(async () => { await LoadLogs(); ConsoleViews.SelectedIndex = 0; });
    private async void CherryClick(object sender, RoutedEventArgs e) => await Safe(() => { Open(Product.ReferralUrl); return Task.CompletedTask; });
    private async void WorkshopClick(object sender, RoutedEventArgs e) => await Safe(() => { Open("https://steamcommunity.com/app/393380/workshop/"); return Task.CompletedTask; });
    private async void CopyAddressClick(object sender, RoutedEventArgs e) => await Safe(() => { if (pool?.Host.Ip is { } ip && Mine is { } mine) { Clipboard.SetText($"{ip}:{mine.Ports.Game}"); Status.Text = L("addressCopied"); } return Task.CompletedTask; });
    private async void ArchiveClick(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        var mine=RequiredMine();var dialog=new Microsoft.Win32.SaveFileDialog{FileName="squad-archive.tar.gz",Filter="gzip (*.tar.gz)|*.tar.gz"};if(dialog.ShowDialog(this)!=true)return;
        using var client=HostingTransport.Client(http!.BaseAddress!,certificateSha256,TimeSpan.FromMinutes(30));client.DefaultRequestHeaders.Authorization=http.DefaultRequestHeaders.Authorization;
        using var response=await client.GetAsync($"api/instances/{mine.Id}/archive",HttpCompletionOption.ResponseHeadersRead);await Check(response);
        var temporary=dialog.FileName+".part-"+Guid.NewGuid().ToString("N");
        try{await using(var file=System.IO.File.Create(temporary)){await response.Content.CopyToAsync(file);}System.IO.File.Move(temporary,dialog.FileName,true);Status.Text=L("archiveSaved");}
        finally{if(System.IO.File.Exists(temporary))System.IO.File.Delete(temporary);}
    });
    private void ConfigClick(object sender, RoutedEventArgs e) => OpenConfiguration();
    private async void AdoptClick(object sender, RoutedEventArgs e) => await Safe(async () => { using var response = await http!.PostAsJsonAsync("api/admin/adopt", new { hostId = ExistingHostId.Text.Trim() }); await Check(response); await Refresh(); });
    private async void RecoveryClick(object sender, RoutedEventArgs e) => await Safe(async () => { using var response = await http!.PostAsJsonAsync("api/admin/recover", new { }); await Check(response); await Refresh(); });
    private async Task RefreshUsers() { if (session?.IsAdmin != true) return; using var response = await http!.GetAsync("api/admin/users"); await Check(response); Users.ItemsSource = (await response.Content.ReadFromJsonAsync<List<UserAccessView>>())!.Select(u => new UserRow(u)).ToList(); }
    private void ShowKey(AccessGrant grant) { if(PersonalHosting)throw new InputException(L("invitationLocal"));IssuedKey.Text = new ConnectionInvite(BaseUri().ToString(),grant.Key,certificateSha256).Encode(); KeyPanel.Visibility = KeyHint.Visibility = Visibility.Visible; }
    private UserAccessView SelectedUser() => Users.SelectedItem is UserRow row && !row.Value.IsAdmin ? row.Value : throw new InputException(L("selectUser"));
    private void UserSelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (RotateButton != null) RotateButton.IsEnabled = RevokeButton.IsEnabled = Users.SelectedItem is UserRow row && !row.Value.IsAdmin; }
    private async void UsersRefreshClick(object sender, RoutedEventArgs e) => await Safe(RefreshUsers);
    private async void CreateUserClick(object sender, RoutedEventArgs e) => await Safe(async () => { if(PersonalHosting)throw new InputException(L("invitationLocal"));if(ParticipantName.Text.Trim().Length is <3 or >40)throw new InputException(L("invalidInput"));ShowKey(await Post<AccessGrant>("api/admin/users", new CreateUserRequest(ParticipantName.Text))); ParticipantName.Clear(); await RefreshUsers(); });
    private async void RotateKeyClick(object sender, RoutedEventArgs e) => await Safe(async () => { var user = SelectedUser(); if (!Confirm(L("rotateConfirm", user.Name), L("newKey"))) return; ShowKey(await Post<AccessGrant>($"api/admin/users/{user.Id}/key", new { })); await RefreshUsers(); });
    private async void RevokeUserClick(object sender, RoutedEventArgs e) => await Safe(async () => { var user = SelectedUser(); if (!Confirm(L("revokeConfirm", user.Name), L("revoke"))) return; using var response = await http!.PostAsJsonAsync($"api/admin/users/{user.Id}/revoke", new { }); await Check(response); IssuedKey.Clear(); KeyPanel.Visibility = KeyHint.Visibility = Visibility.Collapsed; await RefreshUsers(); });
    private void CopyKeyClick(object sender, RoutedEventArgs e) { if (IssuedKey.Text.Length == 0) return; try { Clipboard.SetText(IssuedKey.Text); Status.Text = L("keyCopied"); } catch (Exception error) { Status.Text = L("actionFailed"); ErrorDetails.Text = error.Message; ErrorPanel.Visibility = Visibility.Visible; } }
    private async Task DisconnectLocal()
    {
        if(noticePopup!=null)noticePopup.IsOpen=false;NoticeBadge.Visibility=Visibility.Collapsed;
        SaveServerDraft();draftSaveTimer.Stop();draftPersistenceReady=false;draftFile=null;
        if (hub != null) await hub.DisposeAsync(); hub = null; http?.Dispose(); http = null; session = null;featureApiAvailable=false;budgetLoaded=false;featuresLoadedId=null;seenNotices.Clear();rememberedInvitation=null;certificateSha256=null; pool = null; selectedId = null; draft = false; OwnServers.ItemsSource = null; ClearInstanceViews();
        if (System.IO.File.Exists(Settings)) System.IO.File.Delete(Settings); Servers.ItemsSource = Users.ItemsSource = null; IssuedKey.Clear();
        Output.Text = L("consoleEmpty"); Command.Clear(); ServerPassword.Clear(); ServerName.Text = "My Squad Server"; MaxPlayers.Text = "100"; Mods.Clear(); ErrorDetails.Clear(); ErrorPanel.Visibility = Visibility.Collapsed;
        Workspace.Visibility = StatisticsTab.Visibility = AdminPanel.Visibility = OwnerSettings.Visibility = KeyPanel.Visibility = KeyHint.Visibility = Visibility.Collapsed; LoginPanel.Visibility = Visibility.Visible;PageHeading.Text=""; AccountLabel.Text = ""; displayedInstance = null; DisableActions();
    }
    private async void LogoutClick(object sender, RoutedEventArgs e) => await Safe(async () => { try { if (http != null) { using var response = await http.PostAsync("api/logout", null); await Check(response); } } finally { await DisconnectLocal(); } Status.Text = L("signedOut"); });
    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguagePicker?.SelectedItem is not ComboBoxItem item || item.Tag is not string language) return;
        Localization.Apply(language); configurationEditor?.RefreshLanguage();BuildFeatures(); if (Workspace != null) { VersionLabel.Text = L("developmentVersion"); RenderPool(); RefreshPermissionLabels(); RefreshColumnHeaders(); RenderPlayers(currentPlayers,true); RenderFiles(); RentalCities.Items.Refresh(); }
        if(costChartReport!=null)RenderCostStatistics(costChartReport);
        if(HostingModeHelp!=null)HostingModeHelp.Text=L(PersonalHosting?"personalModeHelp":session?.IsAdmin==true?"sharedOwnerModeHelp":"sharedModeHelp");PersonalAccountButton.Visibility=PersonalHosting?Visibility.Visible:Visibility.Collapsed;
        if (session?.IsAdmin == true && !PersonalHosting) _ = Safe(RefreshUsers);
    }
    private bool startupUpdatesChecked,checkingUpdates;
    private async void UpdateClick(object sender,RoutedEventArgs e)=>await CheckAppUpdates(false);
    private async Task CheckAppUpdates(bool automatic)
    {
        if(checkingUpdates)return;checkingUpdates=true;
        try
        {
            var release=await Updates.Check();
            if(release==null){if(!automatic)UpdateStatus.Text=L("latest");return;}
            while(busy)await Task.Delay(100);
            UpdateStatus.Text=L("availableVersion",release.Version);
            if(!Confirm(L("updateConfirm",release.Version)+"\n\n"+release.Notes,L("updates")))return;
            await Safe(async()=>{Status.Text=L("downloading");await Updates.Install(release,new Progress<long>(bytes=>Status.Text=L("downloading")+" · "+(bytes/1048576d).ToString("0.0")+" MB"));allowClose=true;Close();});
        }
        catch(Exception error){if(!automatic){UpdateStatus.Text=L("updateCheckFailed");AppDialog.Show(this,L("updateCheckFailed")+"\n"+error.Message,L("updates"));}}
        finally{checkingUpdates=false;}
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) { timer.Stop(); return; }
        if (busy) { e.Cancel = true; Status.Text = L("waitClose"); return; }
        if (http == null) { timer.Stop(); return; }
        e.Cancel = true;
        await Safe(async () =>
        {
            await Refresh(); var owned = pool!.Instances.Where(i => i.IsMine && i.Phase is not ("stopped" or "stopping")).ToList();
            if (owned.Count > 0)
            {
                var last = pool.ReleaseEnabled && (pool.OtherActiveServers??pool.Instances.Count(i=>!i.IsMine && i.Phase is not ("stopped" or "stopping")))==0;
                var choice = AppDialog.Show(this, L(last ? "closeAllLast" : "closeAll", owned.Count), L("close"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.Yes)
                    for (var n = 0; n < owned.Count; n++) await Post<OperationView>("api/stop", new StopRequest(Guid.NewGuid(), pool.ReleaseEnabled, owned[n].Id));
            }
            else if (pool.CanRelease)
            {
                var choice = AppDialog.Show(this, L("closeEmpty"), L("close"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.Yes) await Stop(true);
            }
            allowClose = true; Close();
        });
    }
}
