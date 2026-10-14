using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public sealed partial class ConfigEditor : UserControl
{
    public static readonly string[] Files=["Server.cfg","Admins.cfg","MOTD.cfg","ServerMessages.cfg","LayerRotation.cfg","LevelRotation.cfg","ExcludedLayers.cfg","ExcludedLevels.cfg","ExcludedFactions.cfg","LayerVoting.cfg","LayerVotingLowPlayers.cfg","LayerVotingNight.cfg","VoteConfig.cfg","Bans.cfg","RemoteAdminListHosts.cfg","RemoteBanListHosts.cfg","CustomOptions.cfg","ModsConfig.cfg","License.cfg"];
    private sealed record Draft(string Text,bool Pending);
    private sealed record FileChoice(string Name) { public override string ToString()=>Localization.T("cfg_"+Name.Split('.')[0]); }
    private readonly Func<ConfigRequest,Task<string>> send;
    private readonly string storage;
    private readonly bool common;
    private readonly ListBox navigation=new(){MinWidth=170};
    private readonly TextBox raw=new(){AcceptsReturn=true,AcceptsTab=true,MinHeight=280,FontFamily=new("Consolas"),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly StackPanel form=new();
    private readonly TextBlock searchLabel;
    private readonly TextBox search=new(){Margin=new(0,0,0,18)};
    private readonly TextBlock title=new(){FontSize=23,FontWeight=FontWeights.SemiBold},help=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,18)},status=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,0)};
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(650)};
    private readonly DispatcherTimer retryTimer=new(){Interval=TimeSpan.FromSeconds(15)};
    private readonly SemaphoreSlim saving=new(1,1);
    private string? file;
    private bool loading,building,changing;
    public event Action<string,string>? Saved;
    public ConfigEditor(string context,Func<ConfigRequest,Task<string>> send,bool defaults,Func<string,Task<List<ConfigRevision>>>? history=null,Func<string,Task<string>>? stock=null)
    {
        this.send=send;common=defaults;remoteHistory=history;stockFile=stock;
        storage=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SquadHostManager","config-drafts",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context))));
        var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(210)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(24)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});Content=grid;
        navigation.ItemsSource=Files.Select(f=>new FileChoice(f)).ToList();navigation.HorizontalContentAlignment=HorizontalAlignment.Stretch;navigation.SetResourceReference(StyleProperty,"FeatureListStyle");navigation.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"ListBox\"><ScrollViewer Style=\"{DynamicResource NavigationScroller}\" HorizontalScrollBarVisibility=\"Disabled\" VerticalScrollBarVisibility=\"Auto\" CanContentScroll=\"False\"><ItemsPresenter/></ScrollViewer></ControlTemplate>");grid.Children.Add(navigation);
        var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto};scroll.SetResourceReference(StyleProperty,"NavigationScroller");Grid.SetColumn(scroll,2);grid.Children.Add(scroll);
        var panel=new StackPanel{MaxWidth=880,HorizontalAlignment=HorizontalAlignment.Stretch};scroll.Content=panel;
        panel.Children.Add(title);panel.Children.Add(help);
        var scope=Text(defaults?"cfgCommonScope":"cfgServerScope");scope.Margin=new(0,0,0,18);panel.Children.Add(scope);
        var actions=new WrapPanel();panel.Children.Add(actions);
        Button Action(string key,RoutedEventHandler handler){var b=new Button{Margin=new(0,0,8,12)};b.SetResourceReference(ContentControl.ContentProperty,key);b.Click+=handler;actions.Children.Add(b);return b;}
        Action("importConfig",(_,_)=>Import());Action("exportConfig",(_,_)=>Export());Action("cfgSync",async(_,_)=>await Flush());Action("cfgReset",async(_,_)=>await ResetConfig());Action("cfgRollback",async(_,_)=>await RollbackConfig());
        searchLabel=Text("cfgSearch");searchLabel.Margin=new(0,0,0,6);panel.Children.Add(searchLabel);search.SetResourceReference(ToolTipProperty,"cfgSearch");search.TextChanged+=(_,_)=>BuildForm();panel.Children.Add(search);
        status.Margin=new(0,0,0,16);panel.Children.Add(status);panel.Children.Add(form);
        var code=new Expander{IsExpanded=true,Margin=new(0,8,0,0),Content=raw};code.SetResourceReference(HeaderedContentControl.HeaderProperty,"cfgText");panel.Children.Add(code);
        raw.TextChanged+=(_,_)=>{if(loading)return;try{Write(new(raw.Text,true));status.Text=Localization.T("cfgLocalSaved");timer.Stop();timer.Start();if(raw.IsKeyboardFocusWithin)BuildForm();}catch{status.Text=Localization.T("draftSaveFailed");}};
        timer.Tick+=async(_,_)=>{timer.Stop();await Flush();};
        retryTimer.Tick+=async(_,_)=>{if(IsVisible)await Flush();};
        Unloaded+=(_,_)=>{timer.Stop();retryTimer.Stop();_ = Flush();};
        navigation.SelectionChanged+=async(_,_)=>
        {
            if(changing || navigation.SelectedItem is not FileChoice choice)return;
            changing=true;navigation.IsEnabled=false;raw.IsEnabled=form.IsEnabled=false;
            try{timer.Stop();await Flush();await Load(choice.Name);code.IsExpanded=choice.Name is not ("Server.cfg" or "Admins.cfg");}
            finally{navigation.IsEnabled=raw.IsEnabled=form.IsEnabled=true;changing=false;}
        };
        Loaded+=(_,_)=>{retryTimer.Start();if(navigation.SelectedIndex<0)navigation.SelectedIndex=0;};
    }
    private static TextBlock Text(string key){var t=new TextBlock{TextWrapping=TextWrapping.Wrap};t.SetResourceReference(TextBlock.TextProperty,key);return t;}
    private string PathName=>Path.Combine(storage,file+".dat");
    private void Write(Draft draft)
    {
        Directory.CreateDirectory(storage);var temporary=PathName+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temporary,ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(draft),null,DataProtectionScope.CurrentUser));File.Move(temporary,PathName,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private Draft? Read()=>File.Exists(PathName)?JsonSerializer.Deserialize<Draft>(ProtectedData.Unprotect(File.ReadAllBytes(PathName),null,DataProtectionScope.CurrentUser)):null;
    private async Task Load(string name)
    {
        file=name;loading=true;Draft? cached=null;
        title.SetResourceReference(TextBlock.TextProperty,"cfg_"+name.Split('.')[0]);help.SetResourceReference(TextBlock.TextProperty,"cfgHelp_"+name.Split('.')[0]);
        try
        {
            try{cached=Read();}catch{status.Text=Localization.T("draftRestoreFailed");}
            if(cached?.Pending==true){raw.Text=cached.Text;status.Text=Localization.T("cfgLocalSaved");timer.Start();}
            else
            {
                try{raw.Text=await send(new(name));Write(new(raw.Text,false));status.Text=Localization.T("cfgSaved");}
                catch{raw.Text=cached?.Text??StandardConfigs.Get(name);status.Text=Localization.T("cfgOffline");}
            }
            if(common && name=="Server.cfg"){raw.Text=StandardConfigs.SharedServer(raw.Text);if(cached?.Pending==true)Write(new(raw.Text,true));}
            try{Checkpoint(raw.Text);}catch{status.Text=Localization.T("draftSaveFailed");}
            BuildForm();
        }
        finally{loading=false;}
    }
    public async Task Flush()
    {
        if(file==null || loading)return;
        await saving.WaitAsync();
        try
        {
            var draft=Read();if(draft?.Pending!=true)return;
            var name=file;var content=draft.Text;Checkpoint(content);
            if(content.Length>20000 || content.Contains('\0'))throw new ArgumentException();
            if(name=="Server.cfg")SquadConfig.Validate(content);
            status.Text=Localization.T("cfgSaving");await send(new(name,content));
            if(name==file && raw.Text==content){Write(new(content,false));status.Text=Localization.T("cfgSaved");}
            Saved?.Invoke(name,content);
        }
        catch(ArgumentException){status.Text=Localization.T("cfgInvalid");}
        catch{status.Text=Localization.T("cfgOffline");}
        finally{saving.Release();}
    }
    private void BuildForm()
    {
        if(building)return;building=true;
        try
        {
            form.Children.Clear();searchLabel.Visibility=search.Visibility=file=="Server.cfg"?Visibility.Visible:Visibility.Collapsed;if(file=="Admins.cfg"){BuildAdminForm();return;}if(file!="Server.cfg")return;
            foreach(var key in new[]{"ServerName","ServerPassword","MaxPlayers","ShouldAdvertise","NumReservedSlots","PublicQueueLimit","MapRotationMode","RandomizeAtStart","AllowTeamChanges","PreventTeamChangeIfUnbalanced","NumPlayersDiffForTeamChanges","ServerMessageInterval","RecordDemos","AllowPublicClientsToRecord","TKAutoKickEnabled","AutoTKBanNumberTKs","AutoTKBanTime"})
            {
                if(common && key is "ServerName" or "ServerPassword")continue;
                if(search.Text.Length>0 && !(key+" "+Localization.T("cfgField_"+key)+" "+Localization.T("cfgHint_"+key)).Contains(search.Text,StringComparison.CurrentCultureIgnoreCase))continue;
                var row=new Grid{Margin=new(0,0,0,18)};row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=new GridLength(16)});row.ColumnDefinitions.Add(new(){Width=new GridLength(240)});
                var labels=new StackPanel();var label=Text("cfgField_"+key);label.FontWeight=FontWeights.SemiBold;labels.Children.Add(label);var detail=Text("cfgHint_"+key);detail.FontSize=12;detail.Opacity=.7;detail.Margin=new(0,5,0,0);labels.Children.Add(detail);row.Children.Add(labels);
                var value=SquadConfig.Get(raw.Text,key);
                void Update(string next){if(building)return;raw.Text=SquadConfig.Set(raw.Text,key,next);}
                FrameworkElement input;
                if(key is "ShouldAdvertise" or "RandomizeAtStart" or "AllowTeamChanges" or "PreventTeamChangeIfUnbalanced" or "RecordDemos" or "AllowPublicClientsToRecord" or "TKAutoKickEnabled")
                {
                    var toggle=new ComboBox{ItemsSource=new[]{Localization.T("cfgUnset"),Localization.T("cfgOn"),Localization.T("cfgOff")},SelectedIndex=value==null?0:value.Equals("true",StringComparison.OrdinalIgnoreCase)?1:2};
                    toggle.SelectionChanged+=(_,_)=>{if(building)return;if(toggle.SelectedIndex>0)Update(toggle.SelectedIndex==1?"true":"false");else raw.Text=SquadConfig.Remove(raw.Text,key);};input=toggle;
                }
                else if(key=="MapRotationMode")
                {
                    var select=new ComboBox{IsEditable=true,ItemsSource=new[]{"LayerList","LayerList_Randomized","LevelList","LevelList_Randomized"},Text=value??""};select.AddHandler(TextBox.TextChangedEvent,new TextChangedEventHandler((_,_)=>Update(select.Text)));input=select;
                }
                else if(key=="ServerPassword")
                {
                    var secret=new PasswordBox{Password=value??""};secret.PasswordChanged+=(_,_)=>Update(secret.Password);input=secret;
                }
                else
                {
                    var entry=new TextBox{Text=value??"",ToolTip=key};entry.TextChanged+=(_,_)=>Update(entry.Text);input=entry;
                }
                input.VerticalAlignment=VerticalAlignment.Top;Grid.SetColumn(input,2);row.Children.Add(input);form.Children.Add(row);
            }
        }
        finally{building=false;}
    }
    public void RefreshLanguage(){navigation.Items.Refresh();BuildForm();}
    private void Import()
    {
        if(file==null)return;
        try{var dialog=new Microsoft.Win32.OpenFileDialog{Filter="Config (*.cfg)|*.cfg|Text (*.txt)|*.txt"};if(dialog.ShowDialog(Window.GetWindow(this))!=true)return;if(new FileInfo(dialog.FileName).Length>80000)throw new ArgumentException();var text=File.ReadAllText(dialog.FileName);if(text.Length>20000 || text.Contains('\0'))throw new ArgumentException();if(file=="Server.cfg")SquadConfig.Validate(text);raw.Text=text;BuildForm();}
        catch{status.Text=Localization.T("cfgInvalid");}
    }
    private void Export()
    {
        if(file==null)return;
        try{var dialog=new Microsoft.Win32.SaveFileDialog{FileName=file,Filter="Config (*.cfg)|*.cfg"};if(dialog.ShowDialog(Window.GetWindow(this))==true){File.WriteAllText(dialog.FileName,raw.Text);status.Text=Localization.T("fileSaved");}}
        catch{status.Text=Localization.T("draftSaveFailed");}
    }
}
