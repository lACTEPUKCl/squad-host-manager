using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public sealed partial class ConfigEditor
{
    private readonly Func<string,Task<List<ConfigRevision>>>? remoteHistory;
    private readonly Func<string,Task<string>>? stockFile;
    private bool historyBusy;
    private string HistoryPath=>Path.Combine(storage,file+".history.dat");
    private List<ConfigRevision> LocalHistory()=>File.Exists(HistoryPath)?JsonSerializer.Deserialize<List<ConfigRevision>>(ProtectedData.Unprotect(File.ReadAllBytes(HistoryPath),null,DataProtectionScope.CurrentUser))??[]:[];
    private void Checkpoint(string content)
    {
        var entries=LocalHistory();if(entries.FirstOrDefault()?.Content==content)return;
        entries.Insert(0,new(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,content));Directory.CreateDirectory(storage);
        var temp=HistoryPath+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temp,ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(entries.Take(40).ToList()),null,DataProtectionScope.CurrentUser));File.Move(temp,HistoryPath,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private async Task ResetConfig()
    {
        if(file==null || loading || historyBusy)return;
        if(AppDialog.Show(Window.GetWindow(this),Localization.T("cfgResetConfirm"),Localization.T("cfgReset"),MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        historyBusy=true;navigation.IsEnabled=raw.IsEnabled=form.IsEnabled=false;
        try
        {
            timer.Stop();await Flush();Checkpoint(raw.Text);var text=StandardConfigs.Get(file);
            if(stockFile!=null)try{text=await stockFile(file);}catch{ }
            raw.Text=text;BuildForm();await Flush();
        }
        catch{status.Text=Localization.T("draftSaveFailed");}
        finally{historyBusy=false;navigation.IsEnabled=raw.IsEnabled=form.IsEnabled=true;}
    }
    private sealed record RevisionRow(ConfigRevision Value){public override string ToString()=>Value.At.ToLocalTime().ToString("g");}
    private async Task RollbackConfig()
    {
        if(file==null || loading || historyBusy)return;
        historyBusy=true;navigation.IsEnabled=raw.IsEnabled=form.IsEnabled=false;
        try
        {
            timer.Stop();await Flush();Checkpoint(raw.Text);var entries=LocalHistory();
            if(remoteHistory!=null)try{entries.AddRange(await remoteHistory(file));}catch{ }
            var versions=entries.OrderByDescending(v=>v.At).Where(v=>v.Content!=raw.Text).DistinctBy(v=>v.Content).Select(v=>new RevisionRow(v)).ToList();
            if(versions.Count==0){status.Text=Localization.T("noVersions");return;}
            var dialog=new Window{Owner=Window.GetWindow(this),Title=Localization.T("cfgRollback"),Width=820,Height=620,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            var body=new DockPanel{Margin=new(24)};dialog.Content=body;
            var selector=new ComboBox{ItemsSource=versions,SelectedIndex=0,Margin=new(0,0,0,16)};DockPanel.SetDock(selector,Dock.Top);body.Children.Add(selector);
            var restore=new Button{Content=Localization.T("restoreVersion"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,16,0,0)};DockPanel.SetDock(restore,Dock.Bottom);body.Children.Add(restore);
            var preview=new TextBox{IsReadOnly=true,AcceptsReturn=true,FontFamily=new("Consolas"),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,Text=versions[0].Value.Content};body.Children.Add(preview);
            selector.SelectionChanged+=(_,_)=>preview.Text=((RevisionRow)selector.SelectedItem).Value.Content;
            restore.Click+=(_,_)=>{if(AppDialog.Show(dialog,Localization.T("cfgRestoreConfirm"),dialog.Title,MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes)dialog.DialogResult=true;};
            if(dialog.ShowDialog()==true){Checkpoint(raw.Text);raw.Text=((RevisionRow)selector.SelectedItem).Value.Content;BuildForm();await Flush();}
        }
        catch{status.Text=Localization.T("draftRestoreFailed");}
        finally{historyBusy=false;navigation.IsEnabled=raw.IsEnabled=form.IsEnabled=true;}
    }
}
