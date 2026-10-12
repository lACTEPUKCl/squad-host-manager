using System.IO;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public partial class MainWindow
{
    private string? selectedId, adminsLoadedId, adminsOriginal;
    private bool draft, selecting;
    private readonly Dictionary<string, DownloadEstimate> estimates = new();
    private sealed record FileRow(ServerFile Value)
    { public string Path => Value.Path; public string DisplayPath => L(Value.Path.StartsWith("Logs/") ? "logs" : "scoreboards") + " / " + string.Join('/',Value.Path.Split('/').Skip(1)); public string Size => Value.Bytes>=1_000_000_000?$"{Value.Bytes/1_000_000_000d:0.##} GB":Value.Bytes>=1_000_000?$"{Value.Bytes/1_000_000d:0.##} MB":$"{Value.Bytes/1_000d:0.##} KB"; }
    private void RenderSelection()
    {
        var owned = pool!.Instances.Where(i => i.IsMine).ToList();
        if (!draft && selectedId == null) selectedId = owned.LastOrDefault(i => i.Phase != "stopped")?.Id ?? owned.LastOrDefault()?.Id;
        selecting = true;
        if (OwnServers.ItemsSource is not List<InstanceView> previous || !previous.Select(i => (i.Id,i.Name)).SequenceEqual(owned.Select(i => (i.Id,i.Name)))) OwnServers.ItemsSource = owned;
        OwnServers.SelectedValue = selectedId;
        selecting = false;
        var show = !draft && Tabs.SelectedItem != SharedHostTab && owned.Count > 0;
        OwnServerLabel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OwnServers.Visibility = show && owned.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        OwnSingleName.Visibility = show && owned.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        OwnSingleName.Text = owned.Count == 1 ? owned[0].Name : "";
        AdminEditorPanel.IsEnabled = Mine != null && adminsLoadedId == Mine.Id;
        NoServerHelpVisibility=Mine==null && Tabs.SelectedIndex is 2 or 3 or 5 or 6 or 9?Visibility.Visible:Visibility.Collapsed;
    }
    private void ClearInstanceViews()
    {
        Output.Text = LogOutput.Text = L("consoleEmpty"); FollowLogs.IsChecked = false; Command.Clear();
        RenderPlayers([]); files = []; filesLoadedId = playersLoadedId = layersLoadedId = commandsLoadedId = null; CurrentLayer.Text = UpcomingLayer.Text = ""; LayerInput.ItemsSource = null; CommandCatalog.ItemsSource = null; PlayerCount.Text = ""; FileList.ItemsSource = null; AdminsEditor.Clear(); adminsLoadedId = null; adminsOriginal = null; AdminEditorPanel.IsEnabled = false; ClearGroupInputs(); AdminSteamId.Clear(); estimates.Clear();
        InstallTiming.Text = ""; InstallProgress.Visibility = Visibility.Collapsed;
    }
    private bool HasPendingChanges()
    {
        var mine = Mine;
        if(draft && (ServerName.Text!="My Squad Server" || ServerPassword.Password.Length>0 || MaxPlayers.Text!="100" || Mods.Text!=defaultMods))return true;
        return adminsLoadedId != null && AdminsEditor.Text != adminsOriginal || mine != null && displayedInstance == mine.Id &&
            (ServerName.Text != mine.Name || MaxPlayers.Text != mine.MaxPlayers.ToString() || Mods.Text != mine.Mods || ServerPassword.Password != (mine.Password ?? ""));
    }
    private void OwnServerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (selecting || OwnServers.SelectedItem is not InstanceView selected) return;
        if (HasPendingChanges() && !Confirm(L("discardChanges"), L("selectedServer"))) { selecting = true; OwnServers.SelectedValue = selectedId; selecting = false; return; }
        selectedId = selected.Id; draft = false; displayedInstance = null; ClearInstanceViews(); RenderPool(); TabChanged(Tabs,new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new object[0], new object[0]) { Source=Tabs });
    }
    private string defaultMods="";
    private void AddServerClick(object sender, RoutedEventArgs e)
    {
        if (HasPendingChanges() && !Confirm(L("discardChanges"), L("addServer"))) return;
        draft = true; selectedId = null; displayedInstance = null; ClearInstanceViews();
        ServerName.Text = "My Squad Server"; ServerPassword.Clear(); MaxPlayers.Text = "100"; Mods.Text=defaultMods;
        Tabs.SelectedItem = MyServersTab; RenderPool();
    }
    private async Task LoadLogs()
    {
        var id = RequiredMine().Id;
        using var response = await http!.GetAsync($"api/instances/{id}/logs"); await Check(response);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        if (Mine?.Id != id) return;
        var next=result.GetProperty("output").GetString();
        if(LogOutput.Text!=next){var offset=LogOutput.VerticalOffset;var follow=FollowLogs.IsChecked==true && LogOutput.VerticalOffset>=LogOutput.ExtentHeight-LogOutput.ViewportHeight-2;LogOutput.Text=next;if(follow)LogOutput.ScrollToEnd();else LogOutput.ScrollToVerticalOffset(offset);}
    }
    private async void FollowLogsChanged(object sender, RoutedEventArgs e)
    { if (FollowLogs.IsChecked == true && Mine?.Phase is "running" or "starting" or "restarting" or "error") await Safe(LoadLogs); }
    private void SaveLogClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "squad-console.txt", Filter = "Text (*.txt)|*.txt" };
        if (dialog.ShowDialog(this) == true) { try { File.WriteAllText(dialog.FileName, ConsoleViews.SelectedIndex == 0 ? LogOutput.Text : Output.Text); Status.Text = L("fileSaved"); } catch (Exception error) { Status.Text = L("actionFailed"); ErrorDetails.Text = error.Message; ErrorPanel.Visibility = Visibility.Visible; } }
    }
    private void RenderTiming(InstanceView? mine)
    {
        var installing = mine != null && mine.Phase is not ("stopped" or "error") && mine.GameState is not ("gameReady" or "gameUnavailable");
        InstallProgress.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
        var shared=mine?.Phase is "queued" or "starting" && pool?.Host.Phase!="ready" && pool?.Host.Startup?.Stage is "gameCacheDownloading" or "gameCacheVerifying" ? pool.Host.Startup:null;
        var percent=shared?.DownloadPercent??mine?.DownloadPercent;
        InstallProgress.IsIndeterminate = percent == null;
        InstallProgress.Value = percent ?? 0;
        InstallTiming.Text = "";
        if (!installing || mine==null) return;
        var stage = shared?.Stage ?? mine!.GameState ?? mine.Phase;
        if (!estimates.TryGetValue(mine.Id, out var estimate)) estimates[mine.Id] = estimate = new();
        var eta = estimate.Observe(shared!=null?$"cache:{pool!.Host.Id}:{shared.StartedAt:O}:{stage}":mine.ProgressKey??stage, percent, DateTimeOffset.UtcNow);
        var elapsed = mine.StartedAt is { } started ? L("elapsed", Math.Max(0, (int)(DateTimeOffset.UtcNow - started).TotalMinutes)) + "\n" : "";
        InstallTiming.Text = (shared!=null?L("waitingSharedFiles")+"\n"+L(stage)+(percent is {} p?" "+p.ToString("0.00")+"%":"")+"\n":"")+(mine.QueuePosition is { } position ? L("queuePosition",position) + "\n" : "") + elapsed + (eta is { } remaining ? L("stageEta", Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))) : L("etaUnknown"));
    }
    private sealed record ModRow(string Id, string Name) { public override string ToString() => Name == Id ? Id : $"{Name} ({Id})"; }
    private readonly Dictionary<string, string> modNames = new();
    private void UpdateModList() { if (ModList != null) ModList.ItemsSource = Regex.Matches(Mods.Text, @"\d+").Select(m => m.Value).Distinct().Select(id => new ModRow(id, modNames.GetValueOrDefault(id, id))).ToList(); }
    private readonly HashSet<string> requestedModNames = new();
    private void RenderConfiguredMods()
    {
        if (MineMods == null) return;
        var mine = Mine;
        var ids = Regex.Matches(mine?.Mods??Mods.Text, @"\d+").Select(m=>m.Value).Distinct().ToList();
        MineMods.Text = ids.Count == 0 ? L("serverWithoutMods") : L(mine==null?"newServerMods":"configuredServerMods") + "\n" + string.Join("\n", ids.Select(id=>new ModRow(id,modNames.GetValueOrDefault(id,id)).ToString()));
        if(mine!=null && mine.Mods!=Mods.Text)MineMods.Text+="\n"+L("modsUnsaved");
    }
    private async Task FetchConfiguredModNames()
    {
        var ids=Regex.Matches(Mine?.Mods??Mods.Text,@"\d+").Select(m=>m.Value).Distinct().Where(id=>!modNames.ContainsKey(id) && requestedModNames.Add(id)).Take(100).ToList();
        if(ids.Count==0)return;
        try { await FetchModNames(ids); RenderConfiguredMods(); } catch { /* IDs remain visible if Workshop is unavailable. */ }
        finally{foreach(var id in ids)requestedModNames.Remove(id);}
    }
    private void ModsChanged(object sender, TextChangedEventArgs e) { UpdateModList();RenderConfiguredMods();SaveServerDraft(); }
    private async void AddModClick(object sender, RoutedEventArgs e)
    {
        var input = ModInput.Text.Trim();
        var id = Regex.IsMatch(input, @"^\d+$") ? input : Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Host == "steamcommunity.com" ? Regex.Match(uri.Query, @"(?:\?|&)id=(\d+)(?:&|$)").Groups[1].Value : "";
        if (id.Length == 0) { Status.Text = L("modInput"); return; }
        Mods.Text = string.Join(' ', Regex.Matches(Mods.Text + " " + id, @"\d+").Select(m => m.Value).Distinct()); ModInput.Clear(); await BackgroundModNames();
    }
    private void RemoveModClick(object sender, RoutedEventArgs e)
    { if (ModList.SelectedItem is ModRow row) Mods.Text = string.Join(' ', Regex.Matches(Mods.Text, @"\d+").Select(m => m.Value).Where(v => v != row.Id)); }
    private async void ModDetailsClick(object sender, RoutedEventArgs e) => await Safe(RefreshModDetails);
    private async Task RefreshModDetails()
    {
        var ids = Regex.Matches(Mods.Text, @"\d+").Select(m => m.Value).Distinct().Take(100).ToList(); if (ids.Count == 0) return;
        await FetchModNames(ids);
        UpdateModList(); Status.Text = L("modsChecked");
    }
    private async void SaveSettingsClick(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var mine = RequiredMine();
        if (!int.TryParse(MaxPlayers.Text, out var max) || max is < 2 or > 100) throw new InputException(L("invalidPlayers"));
        using var response = await http!.PostAsJsonAsync($"api/instances/{mine.Id}/settings", new ServerSettingsRequest(ServerName.Text, ServerPassword.Password, Mods.Text, max)); await Check(response);defaultMods=Mods.Text;
        ServerPassword.Clear(); displayedInstance = null; await Refresh(); Status.Text = L("restartSettingsHelp");
    });
    private async void LoadAdminsClick(object sender, RoutedEventArgs e) => await Safe(LoadAdmins);
    private async Task LoadAdmins()
    {
        var id = RequiredMine().Id; var output = await Post<System.Text.Json.JsonElement>($"api/instances/{id}/config", new ConfigRequest("Admins.cfg"));
        if (Mine?.Id != id) return; AdminsEditor.Text = output.GetProperty("output").GetString(); adminsLoadedId = id; adminsOriginal = AdminsEditor.Text; AdminEditorPanel.IsEnabled = true; Status.Text = L("fileLoaded", "Admins.cfg");
    }
    private async void SaveAdminsClick(object sender, RoutedEventArgs e) => await Safe(SaveAdmins);
    private async Task SaveAdmins()
    {
        var mine=RequiredMine();if(adminsLoadedId!=mine.Id)throw new InputException(L("loadFirst"));
        var content=AdminsEditor.Text;
        AdminEditorPanel.IsEnabled=false;
        try
        {
            await Post<System.Text.Json.JsonElement>($"api/instances/{mine.Id}/config",new ConfigRequest("Admins.cfg",content));
            if(Mine?.Id==mine.Id){adminsOriginal=content;Status.Text=L("fileSaved");}
        }
        finally{AdminEditorPanel.IsEnabled=Mine?.Id==adminsLoadedId;}
    }
    private async void AddAdminClick(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (Mine?.Id != adminsLoadedId || adminsLoadedId == null) { Status.Text = L("loadFirst"); return; }
        var id = AdminSteamId.Text.Trim(); var group = (AdminGroup.SelectedItem as string ?? "").Trim();
        if (!Regex.IsMatch(id, @"^7656119\d{10}$") || !Regex.IsMatch(group, @"^[\p{L}\p{N}_ -]{1,40}$")) { Status.Text = L("invalidAdmin"); return; }
        if (!Regex.IsMatch(AdminsEditor.Text, @"(?im)^\s*Group=" + Regex.Escape(group) + @":.*$")) { Status.Text = L("groupMissing"); return; }
        AdminsEditor.Text = Regex.Replace(AdminsEditor.Text, @"(?im)^\s*Admin=" + Regex.Escape(id) + @":[^\r\n]*\r?\n?", "").TrimEnd() + $"\nAdmin={id}:{group}\n";
        AdminSteamId.Clear(); await SaveAdmins();
    });
    private async void FilesRefreshClick(object sender, RoutedEventArgs e) => await Safe(LoadFiles);
    private async Task LoadFiles()
    {
        var id = RequiredMine().Id; using var response = await http!.GetAsync($"api/instances/{id}/files"); await Check(response);
        var files = await response.Content.ReadFromJsonAsync<List<ServerFile>>() ?? [];
        if (Mine?.Id == id) { this.files = files; filesLoadedId = id; RenderFiles(); Status.Text = L("filesCount", files.Count); }
    }
    private async void DownloadFileClick(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var mine = RequiredMine(); if (FileList.SelectedItem is not FileRow row) throw new InputException(L("selectFile"));
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(row.Path), Filter = "All files (*.*)|*.*" }; if (dialog.ShowDialog(this) != true) return;
        using var client = new System.Net.Http.HttpClient { BaseAddress = http!.BaseAddress, Timeout = TimeSpan.FromMinutes(4) }; client.DefaultRequestHeaders.Authorization = http.DefaultRequestHeaders.Authorization;
        using var response = await client.GetAsync($"api/instances/{mine.Id}/file?path={Uri.EscapeDataString(row.Path)}"); await Check(response);
        await File.WriteAllBytesAsync(dialog.FileName, await response.Content.ReadAsByteArrayAsync()); Status.Text = L("fileSaved");
    });
    private string? playersLoadedId;
    private async Task RefreshPlayers()
    {
        var id = RequiredMine().Id;
        var output = await Post<System.Text.Json.JsonElement>($"api/instances/{id}/rcon", new CommandRequest("ListPlayers"));
        if (Mine?.Id != id) return;
        var players = SquadPlayers.Parse(output.GetProperty("output").GetString() ?? "");
        RenderPlayers(players); playersLoadedId = id; PlayerCount.Text = L("playersCount", players.Count);
    }
    private async void RefreshPlayersClick(object sender, RoutedEventArgs e) => await Safe(RefreshPlayers);
    private async void PlayerActionClick(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var id = RequiredMine().Id;
        if (playersLoadedId != id || SelectedPlayer() is not SquadPlayer player) throw new InputException(L("selectPlayer"));
        var action = (string)((Button)sender).Tag; var reason = PlayerReason.Text.Trim(); var duration = BanDuration.Text.Trim();
        if (action != "AdminForceTeamChangeById" && (reason.Length == 0 || reason.Any(char.IsControl))) throw new InputException(L("reason"));
        if (action == "AdminBanById" && !Regex.IsMatch(duration, @"^(0|[1-9]\d{0,5}[hdwMy]?)$")) throw new InputException(L("banDuration"));
        if (!Confirm(L("playerActionConfirm", ((Button)sender).Content, player.Name), L("players"))) return;
        var current = await Post<System.Text.Json.JsonElement>($"api/instances/{id}/rcon", new CommandRequest("ListPlayers"));
        if (!SquadPlayers.Parse(current.GetProperty("output").GetString() ?? "").Contains(player)) throw new InputException(L("playerChanged"));
        var command = action + " " + player.Id + (action == "AdminForceTeamChangeById" ? "" : action == "AdminBanById" ? " " + duration + " " + reason : " " + reason);
        await Post<System.Text.Json.JsonElement>($"api/instances/{id}/rcon", new CommandRequest(command)); await RefreshPlayers();
    });
    private async void BroadcastClick(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var message = BroadcastText.Text.Trim(); if (message.Length == 0) throw new InputException(L("reason"));
        await Rcon("AdminBroadcast " + message); BroadcastText.Clear();
    });
    private async void SetLayerClick(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var layer = LayerInput.Text.Trim(); if (!Regex.IsMatch(layer, @"^[A-Za-z0-9_]+(?: [A-Za-z0-9_]+){0,4}$")) throw new InputException(L("layerHelp"));
        if (Confirm(L("layerConfirm", layer), L("nextLayer"))) { await Rcon("AdminSetNextLayer " + layer); await RefreshLayers(); Status.Text = L("layerQueued"); }
    });

    private sealed record AdminGroupRow(string Name, string Permissions);
    private sealed record AdminMemberRow(string SteamId, string Group);
    private void AdminsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (AdminGroups == null || AdminMembers == null) return;
        AdminGroups.ItemsSource = Regex.Matches(AdminsEditor.Text, @"(?im)^\s*Group=([^:\r\n]+):([^\r\n]*)").Select(m => new AdminGroupRow(m.Groups[1].Value.Trim(), m.Groups[2].Value.Split("//")[0].Trim())).ToList();
        var selectedGroup = AdminGroup.SelectedItem as string; AdminGroup.ItemsSource = ((IEnumerable<AdminGroupRow>)AdminGroups.ItemsSource).Select(g => g.Name).ToList(); AdminGroup.SelectedItem = selectedGroup;
        AdminMembers.ItemsSource = Regex.Matches(AdminsEditor.Text, @"(?im)^\s*Admin=(\d+):([^\r\n]*)").Select(m => new AdminMemberRow(m.Groups[1].Value, m.Groups[2].Value.Split("//")[0].Trim())).ToList();
    }
    private void AdminGroupSelected(object sender, SelectionChangedEventArgs e)
    { if (AdminGroups.SelectedItem is AdminGroupRow row) { GroupNameInput.Text = row.Name; GroupPermissionsInput.Text = row.Permissions; AdminGroup.SelectedItem = row.Name; SetPermissionSelection(row.Permissions); } }
    private void AdminMemberSelected(object sender, SelectionChangedEventArgs e)
    { if (AdminMembers.SelectedItem is AdminMemberRow row) { AdminSteamId.Text = row.SteamId; AdminGroup.SelectedItem = row.Group; } }
    private async void SetGroupClick(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (Mine?.Id != adminsLoadedId || adminsLoadedId == null) { Status.Text = L("loadFirst"); return; }
        var name = GroupNameInput.Text.Trim(); var permissions = ReadPermissionSelection();
        if (!Regex.IsMatch(name, @"^[\p{L}\p{N}_ -]{1,40}$") || permissions.Length == 0) { Status.Text = L("groupEditHelp"); return; }
        var pattern = @"(?im)^\s*Group=" + Regex.Escape(name) + @":[^\r\n]*"; var line = $"Group={name}:{permissions}";
        AdminsEditor.Text = Regex.IsMatch(AdminsEditor.Text, pattern) ? Regex.Replace(AdminsEditor.Text, pattern, line) : AdminsEditor.Text.TrimEnd() + "\n" + line + "\n";
        ClearGroupInputs(); await SaveAdmins();
    });
    private void ClearGroupInputs()
    { AdminGroups.SelectedItem = null; GroupNameInput.Clear(); GroupPermissionsInput.Clear(); SetPermissionSelection(""); }
    private async void RemoveAdminClick(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (Mine?.Id != adminsLoadedId || adminsLoadedId == null) { Status.Text = L("loadFirst"); return; }
        var id = AdminSteamId.Text.Trim(); if (!Regex.IsMatch(id, @"^\d{17}$")) { Status.Text = L("invalidAdmin"); return; }
        AdminsEditor.Text = Regex.Replace(AdminsEditor.Text, @"(?im)^\s*Admin=" + Regex.Escape(id) + @":[^\r\n]*\r?\n?", ""); AdminSteamId.Clear(); AdminMembers.SelectedItem = null; AdminGroup.SelectedItem = null; await SaveAdmins();
    });

    private async void RemoveGroupClick(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (Mine?.Id != adminsLoadedId || adminsLoadedId == null) { Status.Text=L("loadFirst");return; }
        var name=GroupNameInput.Text.Trim();if(!Regex.IsMatch(name,@"^[\p{L}\p{N}_ -]{1,40}$")){Status.Text=L("groupEditHelp");return;}
        if(Regex.IsMatch(AdminsEditor.Text,@"(?im)^\s*Admin=\d+:"+Regex.Escape(name)+@"(?:\s*(?://.*)?)?$")){Status.Text=L("groupInUse");return;}
        AdminsEditor.Text=Regex.Replace(AdminsEditor.Text,@"(?im)^\s*Group="+Regex.Escape(name)+@":[^\r\n]*\r?\n?","");ClearGroupInputs();await SaveAdmins();
    });

}
