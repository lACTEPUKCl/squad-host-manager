using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public partial class MainWindow
{
    private readonly LocationAvailabilityWatch locationWatch=new();
    private List<RentalLocation> currentLocations=[];
    private DateTimeOffset locationsChecked;
    private string? watchProfile;
    private System.Windows.Forms.NotifyIcon? availabilityTray;
    private void RestoreLocationWatches()
    {
        if(session==null)return;
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BaseUri()+"|"+session.UserId)));
        if(watchProfile==key)return;
        watchProfile=key;locationWatch.Reset();availabilityTray?.Dispose();availabilityTray=null;LocationAlert.Visibility=Visibility.Collapsed;
        try{locationWatch.Selected.UnionWith(JsonSerializer.Deserialize<string[]>(File.ReadAllText(WatchFile))??[]);}catch{}
        UpdateLocationBell();
    }
    private string WatchFile=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SquadHostManager","location-watch-"+watchProfile+".json");
    private void UpdateLocationBell()
    {
        LocationBell.ToolTip=L("watchCitiesCount",locationWatch.Selected.Count);
        LocationBell.Foreground=(System.Windows.Media.Brush)FindResource(locationWatch.Selected.Count>0?"AccentBrush":"MutedBrush");
        if(locationWatch.Selected.Count==0){availabilityTray?.Dispose();availabilityTray=null;}
    }
    private async Task RefreshLocationAvailability()
    {
        if(http==null || loadingCities || DateTimeOffset.UtcNow-locationsChecked<TimeSpan.FromSeconds(30) || pool?.Host.Id!=null && locationWatch.Selected.Count==0)return;
        try{await LoadLocations();}catch{LocationBell.ToolTip=L("watchRefreshFailed");}
    }
    private void NotifyLocations(List<RentalLocation> available)
    {
        if(available.Count==0)return;
        var message=string.Join("\n",available.Select(r=>r.Name+" · "+L("locationAvailable",r.Available)));
        LocationAlertText.Text=message;LocationAlert.Visibility=Visibility.Visible;
        if(availabilityTray==null)
        {
            availabilityTray=new System.Windows.Forms.NotifyIcon{Icon=System.Drawing.SystemIcons.Information,Text="Squad Server Manager",Visible=true};
            availabilityTray.BalloonTipClicked+=(_,_)=>{Show();if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Activate();Tabs.SelectedItem=SharedHostTab;};
        }
        availabilityTray.BalloonTipTitle=L("watchAvailable");availabilityTray.BalloonTipText=message.Length>240?message[..237]+"…":message;
        availabilityTray.BalloonTipIcon=System.Windows.Forms.ToolTipIcon.Info;availabilityTray.ShowBalloonTip(10000);
        System.Media.SystemSounds.Asterisk.Play();
    }
    private async void LocationBellClick(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        await LoadLocations();
        var window=new Window{Owner=this,Title=L("watchCitiesTitle"),Width=560,Height=620,MaxHeight=SystemParameters.WorkArea.Height-60,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var root=new DockPanel{Margin=new(24)};window.Content=root;
        var help=new TextBlock{Text=L("watchCitiesHelp"),TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,20)};DockPanel.SetDock(help,Dock.Top);root.Children.Add(help);
        var actions=new WrapPanel{Margin=new(0,20,0,0)};DockPanel.SetDock(actions,Dock.Bottom);root.Children.Add(actions);
        var save=new Button{Content=L("saveSettings"),Margin=new(0,0,12,0)};save.SetResourceReference(StyleProperty,"PrimaryButton");actions.Children.Add(save);
        var cancel=new Button{Content=L("watchCancel")};cancel.Click+=(_,_)=>window.Close();actions.Children.Add(cancel);
        var list=new StackPanel();root.Children.Add(new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var boxes=new List<CheckBox>();foreach(var location in currentLocations)
        {var box=new CheckBox{Tag=location.Slug,Content=location.Name+" · "+L(location.Available>0?"locationAvailable":"locationUnavailable",location.Available),IsChecked=locationWatch.Selected.Contains(location.Slug),Margin=new(0,0,0,16)};boxes.Add(box);list.Children.Add(box);}
        save.Click+=(_,_)=>{locationWatch.Selected.Clear();locationWatch.Selected.UnionWith(boxes.Where(b=>b.IsChecked==true).Select(b=>(string)b.Tag));Directory.CreateDirectory(Path.GetDirectoryName(WatchFile)!);File.WriteAllText(WatchFile,JsonSerializer.Serialize(locationWatch.Selected));UpdateLocationBell();window.Close();};
        window.ShowDialog();
    });
    private void DismissLocationAlert(object sender,RoutedEventArgs e)=>LocationAlert.Visibility=Visibility.Collapsed;
}
