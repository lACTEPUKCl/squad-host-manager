using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public partial class MainWindow
{
    private ListBox backupList=null!,matchHistoryList=null!,matchFiles=null!;
    private TextBox backupName=null!,copyName=null!;
    private PasswordBox discordWebhook=null!;
    private CheckBox discordEnabled=null!;
    private TextBlock metricsSummary=null!,discordStatus=null!,matchSummary=null!;
    private Canvas cpuChart=null!,memoryChart=null!,playersChart=null!;
    private List<MetricSample> metricPoints=[];
    private bool discordLoaded;
    private readonly Dictionary<string,Button> toolActions=[];
    private sealed record BackupRow(BackupView Value){public override string ToString()=>Value.Name+"\n"+Value.ServerName+" · "+Value.At.ToLocalTime().ToString("g")+" · "+(Value.Bytes/1048576d).ToString("0.0")+" MiB";}
    private sealed record MatchRow(MatchRecord Value)
    {
        public string Date=>Value.At.ToLocalTime().ToString("g");
        public string Server=>Value.ServerName??"—";
        public bool HasArchive=>Value.ArchiveId!=null;
        public string ArchiveHint=>L(HasArchive?"downloadMatchArchive":"matchArchivePending");
        public override string ToString()=>Date+" · "+Server+" · "+Value.ArchiveId;
    }
    private void BuildServerTools(TabControl tabs)
    {
        toolActions.Clear();
        StackPanel Page(string key){var p=new StackPanel{Margin=new(0,20,20,0)};tabs.Items.Add(new TabItem{Header=L(key),Content=new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});return p;}
        void Text(Panel p,string key){p.Children.Add(new TextBlock{Text=L(key),TextWrapping=TextWrapping.Wrap,MaxWidth=800,HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,0,0,18)});}
        void Field(Panel p,string key,Control c){p.Children.Add(new Label{Content=L(key),Target=c,Padding=new(0)});c.Width=360;c.HorizontalAlignment=HorizontalAlignment.Left;c.Margin=new(0,6,0,16);System.Windows.Automation.AutomationProperties.SetName(c,L(key));p.Children.Add(c);}
        void Button(Panel p,string key,Func<Task> action){var b=new Button{Content=L(key),HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,0,12,12)};toolActions[key]=b;b.Click+=async(_,_)=>{b.IsEnabled=false;try{await Safe(async()=>{await action();await Refresh();await LoadServerTools();});}finally{b.IsEnabled=true;RenderToolsActions();}};p.Children.Add(b);}
        var backups=Page("backups");Text(backups,"backupsHelp");backupName=new(){Text=DateTime.Now.ToString("yyyy-MM-dd HH:mm")};Field(backups,"backupName",backupName);
        Button(backups,"createBackup",async()=>{var mine=RequiredMine();using var client=ToolsClient();await Post<BackupView>($"api/instances/{mine.Id}/backups",new BackupRequest(backupName.Text.Trim()),client);});
        backupList=new(){MinHeight=90,MaxHeight=200};backupList.SelectionChanged+=(_,_)=>RenderToolsActions();backupList.SetResourceReference(StyleProperty,"FeatureListStyle");backups.Children.Add(backupList);
        var actions=new WrapPanel{Margin=new(0,16,0,8)};backups.Children.Add(actions);
        Button(actions,"exportBackup",async()=>{if(backupList.SelectedItem is not BackupRow row)throw new InputException(L("selectBackup"));await ToolsDownload("api/backups/"+row.Value.Id,"squad-"+row.Value.Id[..8]+".shmbackup");});
        Button(actions,"importBackup",async()=>{var open=new Microsoft.Win32.OpenFileDialog{Filter="Squad backup|*.shmbackup"};if(open.ShowDialog(this)!=true)return;if(new FileInfo(open.FileName).Length>512L*1024*1024)throw new InputException(L("backupTooLarge"));using var client=ToolsClient();await using var file=File.OpenRead(open.FileName);using var body=new StreamContent(file);using var response=await client.PostAsync("api/backups/import",body);await Check(response);});
        Button(actions,"deleteBackup",async()=>{if(backupList.SelectedItem is not BackupRow row)throw new InputException(L("selectBackup"));if(!Confirm(L("deleteBackupConfirm",row.Value.Name),L("deleteBackup")))return;using var response=await http!.DeleteAsync("api/backups/"+row.Value.Id);await Check(response);});
        copyName=new();Field(backups,"newServerName",copyName);var copyActions=new WrapPanel();backups.Children.Add(copyActions);
        Button(copyActions,"restoreBackup",async()=>{if(backupList.SelectedItem is not BackupRow row)throw new InputException(L("selectBackup"));if(!Confirm(L("restoreBackupConfirm"),L("restoreBackup")))return;await Post<OperationView>($"api/backups/{row.Value.Id}/restore",new CopyRequest(Guid.NewGuid(),copyName.Text.Trim()));});
        Button(copyActions,"cloneServer",async()=>{var mine=RequiredMine();if(!Confirm(L("cloneServerConfirm"),L("cloneServer")))return;using var client=ToolsClient();await Post<OperationView>($"api/instances/{mine.Id}/clone",new CopyRequest(Guid.NewGuid(),copyName.Text.Trim()),client);});
        var monitoring=Page("monitoring");Text(monitoring,"monitoringHelp");metricsSummary=new(){FontSize=18,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,18)};monitoring.Children.Add(metricsSummary);
        Canvas Chart(string title){Text(monitoring,title);var chart=new Canvas{Height=96,ClipToBounds=true,Margin=new(0,0,0,22)};chart.SizeChanged+=(_,_)=>RenderToolsCharts();monitoring.Children.Add(chart);return chart;}
        cpuChart=Chart("cpuChart");memoryChart=Chart("memoryChart");playersChart=Chart("playersChart");
        var history=Page("matchHistory");Text(history,"savedMatchHistoryHelp");
        var heading=new DockPanel{Margin=new(16,0,16,12)};var dateHeading=new TextBlock{Text=L("matchDate"),Width=164};DockPanel.SetDock(dateHeading,Dock.Left);heading.Children.Add(dateHeading);heading.Children.Add(new TextBlock{Text=L("name")});history.Children.Add(heading);
        matchHistoryList=new(){MinHeight=80,MaxHeight=430};matchHistoryList.SetResourceReference(StyleProperty,"FeatureListStyle");history.Children.Add(matchHistoryList);matchHistoryList.SelectionChanged+=(_,_)=>RenderMatchSelection();
        var row=new FrameworkElementFactory(typeof(DockPanel));
        var delete=new FrameworkElementFactory(typeof(Button));delete.SetValue(ContentControl.ContentProperty,L("deleteMatchArchive"));delete.SetValue(DockPanel.DockProperty,Dock.Right);delete.SetValue(FrameworkElement.MarginProperty,new Thickness(12,0,0,0));delete.AddHandler(System.Windows.Controls.Button.ClickEvent,new RoutedEventHandler(async(sender,_)=>{if(((FrameworkElement)sender).DataContext is not MatchRow selected)return;if(!Confirm(L("deleteMatchArchiveConfirm",selected.Server),L("deleteMatchArchive")))return;await Safe(async()=>{using var response=await http!.DeleteAsync("api/matches/"+selected.Value.Id);await Check(response);await LoadServerTools();});}));row.AppendChild(delete);
        var download=new FrameworkElementFactory(typeof(Button));download.SetValue(ContentControl.ContentProperty,L("downloadMatchArchive"));download.SetValue(DockPanel.DockProperty,Dock.Right);download.SetBinding(IsEnabledProperty,new System.Windows.Data.Binding("HasArchive"));download.SetBinding(ToolTipProperty,new System.Windows.Data.Binding("ArchiveHint"));download.AddHandler(System.Windows.Controls.Button.ClickEvent,new RoutedEventHandler(async(sender,_)=>{if(((FrameworkElement)sender).DataContext is not MatchRow selected)return;await Safe(()=>ToolsDownload("api/matches/"+selected.Value.Id+"/archive","squad-logs-"+selected.Value.At.ToString("yyyy-MM-dd-HHmm")+".tar.gz"));}));row.AppendChild(download);
        var date=new FrameworkElementFactory(typeof(TextBlock));date.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding("Date"));date.SetValue(FrameworkElement.WidthProperty,164d);date.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);date.SetValue(DockPanel.DockProperty,Dock.Left);row.AppendChild(date);
        var name=new FrameworkElementFactory(typeof(TextBlock));name.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding("Server"));name.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);name.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);name.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,16,0));row.AppendChild(name);var rowTemplate=new DataTemplate{VisualTree=row};var historyStyle=new Style(typeof(ListBoxItem),(Style)((Style)FindResource("FeatureListStyle")).Setters.OfType<Setter>().Single(setter=>setter.Property==ItemsControl.ItemContainerStyleProperty).Value);historyStyle.Setters.Add(new Setter(ContentControl.ContentTemplateProperty,rowTemplate));matchHistoryList.ItemContainerStyle=historyStyle;
        matchSummary=new(){Visibility=Visibility.Collapsed,TextWrapping=TextWrapping.Wrap,Margin=new(0,16,0,16)};history.Children.Add(matchSummary);matchFiles=new(){Visibility=Visibility.Collapsed};
        var discord=Page("discordNotifications");Text(discord,"discordHelp");discordEnabled=new(){Content=L("discordEnabled"),Margin=new(0,0,0,16)};discord.Children.Add(discordEnabled);discordWebhook=new();Field(discord,"discordWebhook",discordWebhook);discordStatus=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,16)};discord.Children.Add(discordStatus);
        Button(discord,"save",async()=>{using var response=await http!.PostAsJsonAsync("api/discord",new DiscordSettings(discordWebhook.Password.Length==0?null:discordWebhook.Password.Trim(),discordEnabled.IsChecked==true,Localization.Language));await Check(response);discordWebhook.Clear();discordLoaded=false;});
        Button(discord,"discordTest",async()=>{using var response=await http!.PostAsJsonAsync("api/discord/test",new{});await Check(response);Status.Text=L("discordTestSent");});
        Button(discord,"discordRemove",async()=>{using var response=await http!.PostAsJsonAsync("api/discord",new DiscordSettings("",false,Localization.Language));await Check(response);discordWebhook.Clear();discordLoaded=false;});
        discordLoaded=false;
        RenderToolsActions();
    }
    private void RenderToolsActions()
    {
        var ready=pool?.Host.Phase=="ready" && pool.Host.Id!=null;var own=ready && Mine?.Phase is "running" or "stopped" or "error";
        void Set(string key,bool enabled){if(toolActions.TryGetValue(key,out var b))b.IsEnabled=enabled;}
        Set("createBackup",own);Set("cloneServer",own);Set("restoreBackup",ready && backupList?.SelectedItem is BackupRow);Set("exportBackup",backupList?.SelectedItem is BackupRow);Set("deleteBackup",backupList?.SelectedItem is BackupRow);Set("archive",own);Set("downloadFile",ready && matchFiles?.SelectedItem is ServerFile);
    }
    private HttpClient ToolsClient(){var client=HostingTransport.Client(http!.BaseAddress!,certificateSha256,TimeSpan.FromMinutes(30));client.DefaultRequestHeaders.Authorization=http.DefaultRequestHeaders.Authorization;return client;}
    private async Task ToolsDownload(string url,string filename)
    {
        var save=new Microsoft.Win32.SaveFileDialog{FileName=filename,Filter="All files|*.*"};if(save.ShowDialog(this)!=true)return;
        using var client=ToolsClient();using var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead);await Check(response);var temporary=save.FileName+".part-"+Guid.NewGuid().ToString("N");
        try{await using(var file=File.Create(temporary))await response.Content.CopyToAsync(file);File.Move(temporary,save.FileName,true);Status.Text=L("archiveSaved");}finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private async Task LoadServerTools()
    {
        if(http==null || backupList==null)return;
        using var response=await http.GetAsync("api/server-tools"+(Mine==null?"":"?instanceId="+Mine.Id));if(response.StatusCode==System.Net.HttpStatusCode.NotFound){metricsSummary.Text=L("serviceUpdateRequired");return;}await Check(response);var view=(await response.Content.ReadFromJsonAsync<ServerToolsView>())!;
        var backup=(backupList.SelectedItem as BackupRow)?.Value.Id;var match=(matchHistoryList.SelectedItem as MatchRow)?.Value.Id;
        if(!backupList.Items.Cast<BackupRow>().Select(r=>r.Value).SequenceEqual(view.Backups))backupList.ItemsSource=view.Backups.Select(b=>new BackupRow(b)).ToList();backupList.SelectedItem=backupList.Items.Cast<BackupRow>().FirstOrDefault(b=>b.Value.Id==backup);
        var rows=view.Matches.Select(m=>new MatchRow(m)).ToList();if(!matchHistoryList.Items.Cast<MatchRow>().Select(r=>r.ToString()+r.Value.Id+string.Join(',',r.Value.Files)).SequenceEqual(rows.Select(r=>r.ToString()+r.Value.Id+string.Join(',',r.Value.Files))))matchHistoryList.ItemsSource=rows;matchHistoryList.SelectedItem=matchHistoryList.Items.Cast<MatchRow>().FirstOrDefault(m=>m.Value.Id==match);RenderMatchSelection();
        metricPoints=view.Metrics;var last=metricPoints.LastOrDefault();string Bytes(long? n)=>n is {} b?(b/1073741824d).ToString("0.00")+" GiB":"—";
        metricsSummary.Text=last==null?L("monitoringEmpty"):L("metricSummary",last.CpuPercent?.ToString("0.0")??"—",Bytes(last.MemoryBytes),Bytes(last.MemoryLimit),Bytes(last.DiskBytes),Bytes(last.HostDiskAvailable),last.Players?.ToString()??"—",last.At.ToLocalTime().ToString("T"))+(last.Error==null?"":"\n"+L("monitoringUnavailable"));RenderToolsCharts();
        if(!discordLoaded){discordEnabled.IsChecked=view.Discord.Enabled;discordLoaded=true;}discordStatus.Text=L(view.Discord.Configured?"discordConfigured":"discordNotConfigured")+(view.Discord.LastDelivered is {} at?"\n"+L("discordLastSent",at.ToLocalTime().ToString("g")):"")+(view.Discord.LastError==null?"":"\n"+view.Discord.LastError);
        RenderToolsActions();
    }
    private void RenderMatchSelection()
    {
        if(matchSummary==null)return;if(matchHistoryList.SelectedItem is not MatchRow row){matchSummary.Text=matchHistoryList.Items.Count==0?L("matchHistoryEmpty"):L("selectMatch");matchFiles.ItemsSource=null;return;}
        var m=row.Value;matchSummary.Text=(m.ArchiveId==null?L("matchArchivePending")+"\n":"")+(m.Layer??L("matchLayerUnknown"))+"\n"+(m.Result=="draw"?L("matchDraw"):m.Result??L("matchResultUnknown"))+"\n"+L("matchDuration",m.DurationSeconds is {} seconds?TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss"):"—");matchFiles.DisplayMemberPath="Path";matchFiles.ItemsSource=m.Files;
    }
    private void RenderToolsCharts()
    {
        if(cpuChart==null || memoryChart==null || playersChart==null)return;
        void Draw(Canvas canvas,Func<MetricSample,double?> value,double minimum)
        {
            canvas.Children.Clear();var points=metricPoints.Where(p=>p.At>DateTimeOffset.UtcNow.AddHours(-1)).ToList();if(points.Count<2 || canvas.ActualWidth<10)return;var max=Math.Max(minimum,points.Max(p=>value(p)??0));
            var brush=(Brush)FindResource("AccentBrush");Polyline? line=null;
            var muted=(Brush)FindResource("MutedBrush");
            for(var step=0;step<=2;step++){var y=16+step*(canvas.Height-36)/2;var grid=new Line{X1=40,X2=canvas.ActualWidth,Y1=y,Y2=y,Stroke=muted,Opacity=.15};canvas.Children.Add(grid);var label=new TextBlock{Text=(max*(1-step/2d)).ToString("0.#"),Foreground=muted,FontSize=10};Canvas.SetTop(label,y-7);canvas.Children.Add(label);}
            var duration=Math.Max(1,(points[^1].At-points[0].At).TotalSeconds);
            for(var i=0;i<points.Count;i++){if(value(points[i]) is not {} v){line=null;continue;}if(i>0 && points[i].At-points[i-1].At>TimeSpan.FromSeconds(75))line=null;if(line==null){line=new(){Stroke=brush,StrokeThickness=2};canvas.Children.Add(line);}line.Points.Add(new(40+(points[i].At-points[0].At).TotalSeconds*(canvas.ActualWidth-40)/duration,canvas.Height-20-Math.Clamp(v/max,0,1)*(canvas.Height-36)));}
            foreach(var (sample,right) in new[]{(points[0],false),(points[^1],true)}){var label=new TextBlock{Text=sample.At.ToLocalTime().ToString("HH:mm"),Foreground=muted,FontSize=10};Canvas.SetTop(label,canvas.Height-14);if(right)Canvas.SetRight(label,0);else Canvas.SetLeft(label,40);canvas.Children.Add(label);}
        }
        Draw(cpuChart,m=>m.CpuPercent,200);Draw(memoryChart,m=>m.MemoryBytes is {} n?n/1073741824d:null,12);Draw(playersChart,m=>m.Players,100);
    }
}
