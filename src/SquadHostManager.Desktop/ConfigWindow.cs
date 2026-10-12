using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public sealed class ConfigWindow : Window
{
    private sealed record VersionRow(ConfigVersion Value){public override string ToString()=>Value.At.ToLocalTime().ToString("g");}
    private sealed record ConfigFile(string Name, string Label) { public override string ToString() => Label + " · " + Name; }
    public ConfigWindow(Func<ConfigRequest, Task<string>> send,Func<string,Task<List<ConfigVersion>>>? versions=null,Func<string,Task<string>>? restore=null)
    {
        Title = Localization.T("config"); Width = 880; Height = 660; MinWidth = 650; MinHeight = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(20) }; Content = panel;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = Localization.T("configHelp"), TextWrapping = TextWrapping.Wrap, Margin = new(0,0,0,12) });
        string[] names = ["LayerRotation.cfg","LevelRotation.cfg","ExcludedFactions.cfg","ExcludedLayers.cfg","ExcludedLevels.cfg","LayerVoting.cfg","LayerVotingLowPlayers.cfg","LayerVotingNight.cfg","VoteConfig.cfg","MOTD.cfg","ServerMessages.cfg","Bans.cfg","RemoteAdminListHosts.cfg","RemoteBanListHosts.cfg","CustomOptions.cfg","License.cfg","Admins.cfg"];
        var file = new ComboBox { ItemsSource = names.Select(n => new ConfigFile(n,Localization.T("cfg_"+n.Split('.')[0]))).ToList(),SelectedIndex=0,Margin=new(0,0,0,12) };top.Children.Add(file);
        var buttons = new WrapPanel();top.Children.Add(buttons);
        Button Button(string key){var b=new Button{Content=Localization.T(key),Padding=new(12,6,12,6),Margin=new(0,0,12,12)};buttons.Children.Add(b);return b;}
        var history=Button("configHistory");history.Visibility=versions==null?Visibility.Collapsed:Visibility.Visible;var save=Button("save");var import=Button("importConfig");var export=Button("exportConfig");var retry=Button("refresh");retry.Visibility=Visibility.Collapsed;
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,0)};DockPanel.SetDock(status,Dock.Bottom);panel.Children.Add(status);
        var editor=new TextBox{AcceptsReturn=true,AcceptsTab=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new("Consolas"),Padding=new(12)};panel.Children.Add(editor);
        ConfigFile? loaded=null;string original="";bool busy=false,selecting=false;
        bool Discard() => loaded==null || editor.Text==original || AppDialog.Show(this,Localization.T("discardChanges"),Title,MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;
        async Task Run(bool writing)
        {
            if(busy)return;busy=true;file.IsEnabled=save.IsEnabled=import.IsEnabled=export.IsEnabled=retry.IsEnabled=false;
            try
            {
                var selected=(ConfigFile)file.SelectedItem;status.Text=Localization.T("configLoading");
                if(writing && loaded!=selected)throw new InvalidOperationException(Localization.T("loadFirst"));
                var output=await send(new(selected.Name,writing?editor.Text:null));
                if(!writing){editor.Text=output;loaded=selected;}
                original=editor.Text;status.Text=Localization.T(writing?"fileSaved":"fileLoaded",selected.Name);retry.Visibility=Visibility.Collapsed;
            }
            catch(Exception e){status.Text=e.Message;retry.Visibility=Visibility.Visible;}
            finally{busy=false;file.IsEnabled=import.IsEnabled=retry.IsEnabled=true;save.IsEnabled=export.IsEnabled=loaded==(ConfigFile)file.SelectedItem;}
        }
        file.SelectionChanged+=async(_,_)=>
        {
            if(selecting || !IsLoaded)return;
            if(!Discard()){selecting=true;file.SelectedItem=loaded;selecting=false;return;}
            editor.Clear();loaded=null;await Run(false);
        };
        Loaded+=async(_,_)=>await Run(false);save.Click+=async(_,_)=>await Run(true);retry.Click+=async(_,_)=>await Run(false);
        import.Click+=(_,_)=>
        {
            try
            {
                var dialog=new Microsoft.Win32.OpenFileDialog{Filter="Config (*.cfg)|*.cfg|Text (*.txt)|*.txt"};if(dialog.ShowDialog(this)!=true)return;
                var text=System.IO.File.ReadAllText(dialog.FileName);if(text.Length>20000 || text.Contains('\0'))throw new InvalidOperationException(Localization.T("invalidInput"));
                loaded=(ConfigFile)file.SelectedItem;editor.Text=text;save.IsEnabled=export.IsEnabled=true;status.Text=Localization.T("importHint");
            }
            catch(Exception e){status.Text=e.Message;}
        };
        export.Click+=(_,_)=>
        {
            try{var dialog=new Microsoft.Win32.SaveFileDialog{FileName=loaded!.Name,Filter="Config (*.cfg)|*.cfg"};if(dialog.ShowDialog(this)==true){System.IO.File.WriteAllText(dialog.FileName,editor.Text);status.Text=Localization.T("fileSaved");}}
            catch(Exception e){status.Text=e.Message;}
        };
        history.Click+=async(_,_)=>
        {
            if(busy || loaded==null || !Discard())return;history.IsEnabled=false;
            try
            {
                var entries=await versions!(loaded.Name);var dialog=new Window{Owner=this,Title=Localization.T("configHistory"),Width=800,Height=620,WindowStartupLocation=WindowStartupLocation.CenterOwner};var body=new DockPanel{Margin=new(24)};dialog.Content=body;
                var select=new ComboBox{ItemsSource=entries.Select(v=>new VersionRow(v)).ToList(),SelectedIndex=entries.Count>0?0:-1,Margin=new(0,0,0,16)};DockPanel.SetDock(select,Dock.Top);body.Children.Add(select);
                var recover=new Button{Content=Localization.T("restoreVersion"),HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=entries.Count>0,Margin=new(0,16,0,0)};DockPanel.SetDock(recover,Dock.Bottom);body.Children.Add(recover);
                var preview=new TextBox{IsReadOnly=true,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Text=entries.FirstOrDefault()?.Content??Localization.T("noVersions"),FontFamily=new("Consolas")};body.Children.Add(preview);select.SelectionChanged+=(_,_)=>preview.Text=(select.SelectedItem as VersionRow)?.Value.Content??"";
                var restoring=false;recover.Click+=async(_,_)=>{if(select.SelectedItem is not VersionRow selected || AppDialog.Show(dialog,Localization.T("restoreConfirm"),dialog.Title,MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;restoring=true;recover.IsEnabled=false;try{await restore!(selected.Value.Id);editor.Text=selected.Value.Content;original=editor.Text;status.Text=Localization.T("fileSaved",loaded.Name);restoring=false;dialog.Close();}catch(Exception error){AppDialog.Show(dialog,error.Message);restoring=false;recover.IsEnabled=true;}};dialog.Closing+=(_,args)=>args.Cancel=restoring;dialog.ShowDialog();
            }
            catch(Exception error){status.Text=error.Message;}
            finally{history.IsEnabled=true;}
        };
        Closing+=(_,e)=>{if(busy || !Discard())e.Cancel=true;};
    }
}
