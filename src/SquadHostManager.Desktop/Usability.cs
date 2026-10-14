using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public partial class MainWindow
{
    private bool pendingSnapshot;
    private bool backgroundRefreshing;
    private DateTimeOffset lastBackgroundRefresh,lastBackgroundFeatures,backgroundRetryAt;
    private async Task BackgroundRefresh(bool logs = false)
    {
        if (http == null || DateTimeOffset.UtcNow<backgroundRetryAt) return;
        if(busy || refreshing || backgroundRefreshing) { pendingSnapshot=true; return; }
        if(DateTimeOffset.UtcNow-lastBackgroundRefresh<TimeSpan.FromSeconds(3))return;
        pendingSnapshot=false;backgroundRefreshing=true;lastBackgroundRefresh=DateTimeOffset.UtcNow;
        try { await RestoreEvents(); await Refresh(); await LoadBalance(); await RefreshLocationAvailability(); if(featureApiAvailable && DateTimeOffset.UtcNow-lastBackgroundFeatures>=TimeSpan.FromSeconds(30)){lastBackgroundFeatures=DateTimeOffset.UtcNow;await LoadFeatures();} if(Tabs.SelectedIndex==5 && adminsLoadedId!=AdminTarget && (Mine==null || Mine.Phase is "running" or "stopped")) await LoadAdmins(); if (logs && Tabs.SelectedIndex==2 && FollowLogs.IsChecked == true && Mine?.Phase is "running" or "starting" or "restarting" or "error") await LoadLogs(); if(Tabs.SelectedIndex==3 && Mine?.GameState=="gameReady") await RefreshPlayers(); if(Tabs.SelectedItem==StatisticsTab && session?.IsAdmin==true)await LoadStatistics(); }
        catch(RequestException e) when(e.Code==System.Net.HttpStatusCode.TooManyRequests){backgroundRetryAt=DateTimeOffset.UtcNow.AddSeconds(30);}
        catch (Exception e) { Status.Text = e is InputException ? e.Message : e is RequestException request ? request.UserMessage : e is HttpRequestException ? L("networkLost") : L("actionFailed"); ErrorDetails.Text = e.Message; ErrorPanel.Visibility = Visibility.Visible; }
        finally { backgroundRefreshing=false;if(pendingSnapshot && !busy && http!=null) _ = Dispatcher.BeginInvoke(new Action(async()=>await BackgroundRefresh())); }
    }
    private bool restoringEvents;
    private async Task RestoreEvents()
    {
        var connection=hub;if(connection==null || connection.State!=Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Disconnected || restoringEvents)return;
        restoringEvents=true;
        try{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));await connection.StartAsync(timeout.Token);}catch{}finally{restoringEvents=false;}
    }
    private List<SquadPlayer> currentPlayers = [];
    private void RefreshColumnHeaders()
    {
        void Set(DataGrid grid, params string[] keys) { for(var i=0;i<keys.Length;i++)grid.Columns[i].Header=keys[i] is "Game" or "Query" or "Steam ID" ? keys[i] : L(keys[i]); }
        Set(Servers,"name","owner","state","Game","Query"); Set(Users,"username","state");
        Set(PlayerList,"playerName","Steam ID");Set(PlayerListTwo,"playerName","Steam ID");
        Set(StatisticsUsers,"username","currency","spent","separateHostsCost","savedMoney");
        Set(AdminGroups,"adminGroup","permissions");Set(AdminMembers,"Steam ID","adminGroup");Set(FileList,"filePath","fileSize");
    }
    private bool loadingTab;
    private async void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        if(PageHeading!=null && Tabs.SelectedItem is TabItem page)PageHeading.Text=page.Header?.ToString()??"";
        if (pool != null) RenderSelection();
        if(Tabs.SelectedItem==ConfigurationTab && http!=null){if(configurationEditor==null || configurationContext!=ConfigurationContext)BuildConfigurationPage();return;}
        if (http == null || Mine == null && Tabs.SelectedItem!=StatisticsTab && Tabs.SelectedItem!=FeaturesTab && Tabs.SelectedIndex!=5 || loadingTab) return;
        loadingTab = true;
        try
        {
            await Safe(async () =>
            {
                if(Tabs.SelectedItem==FeaturesTab)await LoadFeatures();
                else if (Tabs.SelectedIndex == 2 && Mine?.Phase is "running" or "starting" or "restarting" or "error") await LoadLogs();
                else if (Tabs.SelectedIndex == 3 && Mine?.GameState == "gameReady") await RefreshPlayers();
                else if (Tabs.SelectedIndex == 4) await RefreshModDetails();
                else if (Tabs.SelectedIndex == 5 && adminsLoadedId != AdminTarget && (Mine==null || Mine.Phase is "running" or "stopped")) await LoadAdmins();
                else if (Tabs.SelectedIndex == 6 && filesLoadedId != Mine?.Id && Mine?.Phase is "running" or "stopped") await LoadFiles();
                else if (Tabs.SelectedIndex == 10 && session?.IsAdmin==true) await LoadStatistics();
                else if (Tabs.SelectedIndex == 9 && Mine?.GameState == "gameReady") { await RefreshLayers(); await LoadMatchCommands(); }
            });
        }
        finally { loadingTab = false; }
    }
    private sealed record PlayerRow(SquadPlayer Value)
    {
        public string DisplayName => (Value.IsLeader ? "★ " : "") + Value.Name;
        public string SteamId => Value.SteamId;
        public string Squad => Value.Squad is "N/A" or "0" ? L("noSquad") : L("squadNumber", Value.Squad);
    }
    private static ICollectionView PlayerView(IEnumerable<SquadPlayer> players)
    {
        var view = new ListCollectionView(players.OrderBy(p => int.TryParse(p.Squad, out var n) ? n : int.MaxValue).ThenByDescending(p => p.IsLeader).ThenBy(p => p.Name).Select(p => new PlayerRow(p)).ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PlayerRow.Squad))); return view;
    }
    private void RenderPlayers(List<SquadPlayer> players,bool force=false)
    {
        if(!force && PlayerList.ItemsSource!=null && currentPlayers.SequenceEqual(players))return;
        currentPlayers = players;
        var selected = SelectedPlayer();
        PlayerList.ItemsSource = PlayerView(players.Where(p => p.Team == "1")); PlayerListTwo.ItemsSource = PlayerView(players.Where(p => p.Team == "2"));
        TeamOneLabel.Text = L("teamCount",1,players.Count(p => p.Team == "1")); TeamTwoLabel.Text = L("teamCount",2,players.Count(p => p.Team == "2"));
        if(selected!=null)
        {
            foreach(var grid in new[]{PlayerList,PlayerListTwo})
                if(grid.Items.Cast<PlayerRow>().FirstOrDefault(p=>p.Value.Id==selected.Id && p.Value.SteamId==selected.SteamId) is {} row) grid.SelectedItem=row;
        }
    }
    private void PlayerGridSized(object sender, SizeChangedEventArgs e)
    { var grid=(DataGrid)sender; if(grid.Columns.Count>0)grid.Columns[0].Width=new DataGridLength(Math.Max(180,grid.ActualWidth-170)); }
    private void PlayerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlayerListTwo == null) return;
        if (sender == PlayerList && PlayerList.SelectedItem != null) PlayerListTwo.SelectedItem = null;
        if (sender == PlayerListTwo && PlayerListTwo.SelectedItem != null) PlayerList.SelectedItem = null;
    }
    private SquadPlayer? SelectedPlayer() => (PlayerList.SelectedItem as PlayerRow ?? PlayerListTwo.SelectedItem as PlayerRow)?.Value;
    private List<ServerFile> files = [];
    private string? filesLoadedId, layersLoadedId;
    private void FileCategoryChanged(object sender, SelectionChangedEventArgs e) => RenderFiles();
    private void RenderFiles()
    {
        if (FileList == null) return;
        var prefix = FileCategory.SelectedIndex switch {1 => "Logs/",2 => "OSI_Scoreboards/",_ => ""};
        FileList.ItemsSource = files.Where(f => (f.Path.StartsWith("Logs/",StringComparison.Ordinal) || f.Path.StartsWith("OSI_Scoreboards/",StringComparison.Ordinal)) && f.Path.StartsWith(prefix,StringComparison.Ordinal)).Select(f => new FileRow(f)).ToList();
    }
    private async Task BackgroundModNames()
    { try { await RefreshModDetails(); } catch (Exception e) { Status.Text = L("workshopUnavailable"); ErrorDetails.Text = e.Message; ErrorPanel.Visibility = Visibility.Visible; } }
    private async void SaveModsClick(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var mine = RequiredMine(); using var response = await http!.PostAsJsonAsync($"api/instances/{mine.Id}/settings", new ServerSettingsRequest(mine.Name, null, Mods.Text, mine.MaxPlayers));
        await Check(response); await Refresh(); Status.Text = L("restartSettingsHelp");
    });
    private async void SearchModsClick(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("SquadHostManager/0.1");
        var html = await client.GetStringAsync("https://steamcommunity.com/workshop/browse/?appid=393380&section=readytouseitems&browsesort=textsearch&numperpage=30&searchtext=" + Uri.EscapeDataString(ModSearch.Text.Trim()));
        var ids = Regex.Matches(html, @"filedetails/\?id=(\d+)").Select(m => m.Groups[1].Value).Distinct().Take(30).ToList();
        await FetchModNames(ids); ModSearchResults.ItemsSource = ids.Select(id => new ModRow(id,modNames.GetValueOrDefault(id,id))).ToList();
        Status.Text = L(ids.Count == 0 ? "noModsFound" : "modsChecked");
    });
    private async void AddSearchModClick(object sender, RoutedEventArgs e)
    { if (ModSearchResults.SelectedItem is ModRow row) { ModInput.Text = row.Id; AddModClick(sender,e); } await Task.CompletedTask; }
    private async Task FetchModNames(List<string> ids)
    {
        if (ids.Count == 0) return;
        var fields = new List<KeyValuePair<string,string>> {new("itemcount",ids.Count.ToString())};
        for (var i=0;i<ids.Count;i++) fields.Add(new($"publishedfileids[{i}]",ids[i]));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await client.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/",new FormUrlEncodedContent(fields)); response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var item in data.GetProperty("response").GetProperty("publishedfiledetails").EnumerateArray())
        {
            var id=item.GetProperty("publishedfileid").GetString()!;
            modNames[id]=item.GetProperty("result").GetInt32()==1 && item.TryGetProperty("consumer_app_id",out var app) && app.GetInt32()==393380 ? item.GetProperty("title").GetString()??id : L("invalidMod");
        }
        UpdateModList();
    }
    internal static readonly string[] Permissions = ["changemap","cheat","private","config","clientdemos","pause","canseeadminchat","teamchange","forceteamchange","chat","kick","ban","balance","immune","manageserver","featuretest","reserve","demos","debug","cameraman"];
    private string[] unknownPermissions = [];
    private void InitializePermissions()
    {
        foreach (var permission in Permissions) PermissionFlags.Children.Add(new CheckBox {Tag=permission,Content=L("permission_"+permission),ToolTip=permission,Margin=new Thickness(0,0,18,10),MinWidth=210});
    }
    private void RefreshPermissionLabels()
    { foreach (CheckBox box in PermissionFlags.Children) box.Content=L("permission_"+box.Tag); }
    private void SetPermissionSelection(string permissions)
    {
        var selected=permissions.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        unknownPermissions=selected.Where(p=>!Permissions.Contains(p)).ToArray();
        foreach (CheckBox box in PermissionFlags.Children) box.IsChecked=selected.Contains((string)box.Tag);
    }
    private string ReadPermissionSelection() => string.Join(',',PermissionFlags.Children.Cast<CheckBox>().Where(b=>b.IsChecked==true).Select(b=>(string)b.Tag).Concat(unknownPermissions).Distinct());
    private List<string> commands = [];
    private async void ListCommandsClick(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        var output=await Post<JsonElement>($"api/instances/{RequiredMine().Id}/rcon",new CommandRequest("ListCommands 1"));
        var text=output.GetProperty("output").GetString()??"";
        commands=text.Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Where(line=>!Regex.IsMatch(line,@"^AdminPauseMatch(?:\s|$)",RegexOptions.IgnoreCase)).ToList(); FilterCommands(); Output.Text=string.Join('\n',commands);
    });
    private void FilterCommands() { if(CommandCatalog!=null) CommandCatalog.ItemsSource=commands.Where(c=>c.Contains(CommandSearch.Text,StringComparison.OrdinalIgnoreCase)).ToList(); }
    private void CommandSearchChanged(object sender,TextChangedEventArgs e)=>FilterCommands();
    private void CatalogSelected(object sender,SelectionChangedEventArgs e)
    { if(CommandCatalog.SelectedItem is string line) { var match=Regex.Match(line,@"\b(Admin[A-Za-z]+|List[A-Za-z]+|Show[A-Za-z]+)\b"); if(match.Success){Command.Text=match.Value+" ";Command.Focus();Command.CaretIndex=Command.Text.Length;} } }
    private async Task RefreshLayers()
    {
        var id=RequiredMine().Id;
        async Task<string> Query(string command) => (await Post<JsonElement>($"api/instances/{id}/rcon",new CommandRequest(command))).GetProperty("output").GetString()??"";
        var current=await Query("ShowCurrentMap");var next=await Query("ShowNextMap");
        string list;
        try { list=await Query("ListLayers"); } catch { list=""; }
        var rotation=(await Post<JsonElement>($"api/instances/{id}/config",new ConfigRequest("LayerRotation.cfg"))).GetProperty("output").GetString()??"";
        list += "\n" + string.Join('\n',rotation.Split('\n').Select(l=>l.Split("//")[0]));
        if(Mine?.Id!=id)return;
        CurrentLayer.Text=L("currentLayer",current.Trim()); UpcomingLayer.Text=L("upcomingLayer",next.Trim());
        var previous=LayerInput.Text; LayerInput.ItemsSource=Regex.Matches(list,@"\b[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+\b").Select(m=>m.Value).Distinct().Order().ToList();LayerInput.Text=previous;layersLoadedId=id;
    }
    private async void RefreshLayersClick(object sender,RoutedEventArgs e)=>await Safe(RefreshLayers);
    private async void ChangeLayerNowClick(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        var layer=LayerInput.Text.Trim();if(!Regex.IsMatch(layer,@"^[A-Za-z0-9_]+(?: [A-Za-z0-9_]+){0,4}$"))throw new InputException(L("layerHelp"));
        if(Confirm(L("changeLayerConfirm",layer),L("match"))){await Rcon("AdminChangeLayer "+layer);await RefreshLayers();}
    });
    private async void ClearNextLayerClick(object sender,RoutedEventArgs e)=>await Safe(async()=>{await Rcon("AdminClearNextLayer");await RefreshLayers();});
}

