using System.Net;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;
namespace SquadHostManager.Desktop;
public partial class MainWindow
{
    private HostingProviderView? hostingProvider;
    private RespawnView? respawnView;
    private TextBlock? respawnInfo,respawnOffer;
    private Button? providerApply,respawnRent;
    private string? respawnHourlyLabel,respawnSpentLabel;
    private bool loadingProvider;
    private DateTimeOffset providerRefreshed;
    private void BuildHostingProviders()
    {
        if(providerApply!=null || ProviderSelectorPanel==null)return;
        providerApply=new(){Content=L("providerSwitch"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,0,0,14)};ProviderSelectorPanel.Children.Add(providerApply);
        providerApply.Click+=async(_,_)=>await Safe(async()=>
        {
            if(hostingProvider?.CanSwitch!=true)return;
            var target=hostingProvider.Selected=="cherry"?"respawn":"cherry";
            await Post<HostingProviderView>("api/hosting-provider",new HostingProviderRequest(target));
            selectedId=null;displayedInstance=null;ClearInstanceViews();
            await Refresh();await LoadHostingProviders(true);
        });
        var panel=RespawnHostingPanel;
        panel.Children.Add(new TextBlock{Text=L("respawnIntro"),TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,18)});
        panel.Children.Add(new TextBlock{Text=L("respawnPanelHelp"),TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,18)});
        respawnOffer=new(){Text=L("respawnFixedPlan"),FontSize=18,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,18)};panel.Children.Add(respawnOffer);
        respawnRent=new(){Content=L("respawnRent"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,0,0,18)};panel.Children.Add(respawnRent);
        respawnRent.Click+=async(_,_)=>await Safe(async()=>
        {
            if(respawnView?.Plans.SingleOrDefault(p=>p.MemoryMb==8192) is not RespawnPlan plan)return;
            if(!Confirm(L("respawnRentConfirm",8,plan.HourlyPrice.ToString("0.0000"),L("respawnGermany")),L("respawnRent")))return;
            using var client=ToolsClient();await Post<OperationView>("api/respawn/rent",new RespawnRentRequest(Guid.NewGuid(),plan.Id,plan.HourlyPrice,"eu",new ServerSettingsRequest(string.IsNullOrWhiteSpace(ServerName.Text)?"My Squad Server":ServerName.Text,ServerPassword.Password,Mods.Text,int.TryParse(MaxPlayers.Text,out var count)?count:100,Mine?.StartLayer??"")),client);selectedId=null;draft=false;await Refresh();await LoadHostingProviders(true);Tabs.SelectedItem=MyServersTab;
        });
        respawnInfo=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,18)};panel.Children.Add(respawnInfo);
        var powers=new WrapPanel();panel.Children.Add(powers);
        var refresh=new Button{Content=L("refresh"),Margin=new(0,0,0,12)};powers.Children.Add(refresh);refresh.Click+=async(_,_)=>await Safe(()=>LoadHostingProviders(true));
    }
    private async Task LoadHostingProviders(bool force=false)
    {
        if(http==null || loadingProvider || !force && DateTimeOffset.UtcNow-providerRefreshed<TimeSpan.FromSeconds(15))return;
        loadingProvider=true;
        try
        {
            using var response=await http.GetAsync("api/hosting-provider");
            if(response.StatusCode==HttpStatusCode.NotFound){providerRefreshed=DateTimeOffset.UtcNow;return;}
            await Check(response);hostingProvider=await response.Content.ReadFromJsonAsync<HostingProviderView>();providerRefreshed=DateTimeOffset.UtcNow;
            BuildHostingProviders();
            if(hostingProvider?.Selected=="respawn")
            {
                using var r=await http.GetAsync("api/respawn");await Check(r);respawnView=await r.Content.ReadFromJsonAsync<RespawnView>();
                var plan=respawnView!.Plans.SingleOrDefault(p=>p.MemoryMb==8192);
                respawnOffer!.Text=L("respawnFixedPlan")+(plan==null?"":" · "+plan.HourlyPrice.ToString("0.0000")+" EUR / "+L("hourShort"));
                respawnInfo!.Text=L(respawnView.PendingOrder?"respawnOrderPending":respawnView.Configured?"respawnReady":"respawnTokenNeeded");
                respawnRent!.IsEnabled=respawnView.Configured && !respawnView.PendingOrder;

            }
        }
        finally{loadingProvider=false;RenderHostingProvider();}
    }
    private void RenderHostingProvider()
    {
        if(hostingProvider==null || providerApply==null)return;
        var respawn=hostingProvider.Selected=="respawn";
        ProviderSelectorPanel.Visibility=hostingProvider.CanSwitch?Visibility.Visible:Visibility.Collapsed;
        providerApply.IsEnabled=hostingProvider.CanSwitch;
        providerApply.Content=L("providerSwitchTo",respawn?"Cherry Servers":"RespawnHost");
        HostingLayout.Visibility=respawn?Visibility.Collapsed:Visibility.Visible;
        RespawnHostingPanel.Visibility=respawn?Visibility.Visible:Visibility.Collapsed;RespawnHostingPanel.IsEnabled=true;
        if(respawnRent!=null)respawnRent.IsEnabled=respawnView?.Configured==true && respawnView.PendingOrder==false && respawnView.Plans.Count(p=>p.MemoryMb==8192)==1 && pool?.CanRent==true;
        AccountBalanceLabel.Text=respawn?"RespawnHost":L("balanceLabel");
        AccountBalanceLabel.Visibility=AccountBalance.Visibility=respawn?Visibility.Collapsed:Visibility.Visible;
        OverviewBillingNote.Visibility=respawn?Visibility.Visible:Visibility.Collapsed;
        OverviewSpentLabel.Text=L(respawn && session?.IsAdmin==true?"respawnSpentTotal":"hostPersonalSpent");
        foreach(var column in Servers.Columns.OfType<DataGridBoundColumn>())
            if(column.Binding is System.Windows.Data.Binding binding && binding.Path?.Path.StartsWith("Ports.",StringComparison.Ordinal)==true)
                column.Visibility=respawn?Visibility.Collapsed:Visibility.Visible;
        if(respawn)
        {
            HostStatus.Text="RespawnHost";
            RentHostButton.IsEnabled=false;ReleaseButton.Visibility=Visibility.Collapsed;
            OverviewAddress.Text=Mine?.Address??"RespawnHost";OverviewLocation.Text="";OverviewRenter.Text="";OverviewRental.Visibility=Visibility.Visible;OverviewHourly.Text=respawnHourlyLabel??"—";OverviewSpent.Text=respawnSpentLabel??"—";
        }
    }
}
