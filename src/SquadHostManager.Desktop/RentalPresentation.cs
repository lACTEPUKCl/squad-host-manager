using System.Windows;
using System.Windows.Controls;
using System.Net.Http.Json;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;
public partial class MainWindow
{
    private async void RetryHostClick(object sender,RoutedEventArgs e)=>await Safe(async()=>{await Post<OperationView>("api/admin/prepare",new RestartRequest(Guid.NewGuid()));await Refresh();});
    private readonly DownloadEstimate hostDownloadEstimate = new();
    private void RenderRental()
    {
        if(pool==null)return;
        RentalCities.IsEnabled=pool.Host.Id==null && !pool.Busy;
        var h=pool.Host;var r=h.Rental;var now=DateTimeOffset.UtcNow;
        var rentalVisibility=h.Id==null ? Visibility.Visible : Visibility.Collapsed;
        RentalHistoryPanel.Visibility=session?.IsAdmin==true?rentalVisibility:Visibility.Collapsed;
        var showHistory=RentalHistoryPanel.Visibility==Visibility.Visible;
        HostingLayout.ColumnDefinitions[1].Width=new GridLength(showHistory?32:0);
        HostingLayout.ColumnDefinitions[2].Width=showHistory?new GridLength(1,GridUnitType.Star):new GridLength(0);
        RentalCitiesPanel.Visibility=pool.CanRent?rentalVisibility:Visibility.Collapsed;
        RentalPermissionHelp.Visibility=h.Id==null && !pool.CanRent?Visibility.Visible:Visibility.Collapsed;RentalPermissionHelp.Text=L(session?.IsAdmin==true?"rentPaused":"rentPermissionRequired");
        RentalDescription.Visibility=RentalCosts.Visibility=Visibility.Collapsed;
        SharedHostSummary.Visibility=h.Id!=null && session?.IsAdmin!=true?Visibility.Visible:Visibility.Collapsed;
        SharedHostModel.Text=string.IsNullOrWhiteSpace(r?.Model)?L("hostOverview")+" #"+h.Id:r.Model;
        SharedHostInfo.Text=string.Join(" · ",new[]{r?.Location,h.Ip}.Where(v=>!string.IsNullOrWhiteSpace(v)))
            +"\n"+string.Join(" · ",new[]{r?.Cores is {} sharedCores?L("rentalCores",sharedCores):null,r?.Memory,r?.Storage}.Where(v=>!string.IsNullOrWhiteSpace(v)));
        SharedHostRate.Text=r?.HourlyRate is {} sharedRate?sharedRate.ToString("0.000")+" "+r.Currency+" / "+L("hourUnit"):L("rentalUnknown");

        var occupied=pool.OccupiedServers??pool.Instances.Count(i=>i.Phase!="stopped");
        var key=pool.Busy && h.Id==null ? "ordering" : h.Phase is "ordering" or "provisioning" or "preparing" ? h.Phase : h.Id==null?(r?.EndedAt!=null?"hostCancelled":"hostNotRented") : !h.Verified ? "hostConnectionLost" : h.Phase=="ready" ? "hostConnectedSimple" : h.Phase;
        HostStatus.Text=L(key);
        var startup=h.Startup;
        RetryHostButton.Visibility=session?.IsAdmin==true && h.Id!=null && startup?.Stage=="hostFailed" && !pool.Busy?Visibility.Visible:Visibility.Collapsed;
        HostLivePanel.Visibility=startup!=null && (h.Id!=null || pool.Busy && startup.Stage!="hostReady") && (session?.IsAdmin==true || pool.Busy || startup.Stage=="hostFailed" || h.Id!=null && h.RentedByName==session?.Name)?Visibility.Visible:Visibility.Collapsed;
        if(startup!=null)
        {
            HostLivePhase.Text=L(h.Phase=="unavailable" || h.Phase=="ready" && !h.Verified ? "hostConnectionLost" : startup.Stage);
            HostLiveActivity.IsIndeterminate=startup.DownloadPercent==null; HostLiveActivity.Value=startup.DownloadPercent??0;
            HostLivePhase.Text+=startup.DownloadPercent is {} progress ? " · "+progress.ToString("0.00")+"%" : "";
            HostLiveActivity.Visibility=startup.Stage is "hostFailed" or "hostReady"?Visibility.Collapsed:Visibility.Visible;
            HostLiveInfo.Text=L("hostLiveInfo",h.Id??"…",h.Ip??"…",startup.ProviderState??"…");
            if(h.Id!=null && r!=null)HostLiveInfo.Text+="\n"+string.Join(" · ",new[]{r.Model,r.Memory,r.Storage,r.Location}.Where(s=>!string.IsNullOrWhiteSpace(s)))+(r.HourlyRate is {} hourly?" · "+L("rentalQuotedRate",hourly.ToString("0.000")+" "+r.Currency):"");
            HostLiveTiming.Text=L("hostLiveTiming",Math.Max(0,(int)(now-startup.StartedAt).TotalMinutes),startup.UpdatedAt.ToLocalTime().ToString("T"));
            var eta=hostDownloadEstimate.Observe(startup.Stage,startup.DownloadPercent,now);
            if(startup.DownloadPercent!=null)HostLiveTiming.Text+="\n"+(eta is {} remaining?L("stageEta",Math.Max(1,(int)Math.Ceiling(remaining.TotalMinutes))):L("etaUnknown"));
            HostLiveJournal.Text=string.Join('\n',startup.Events.Select(e=>e.At.ToLocalTime().ToString("T")+"  "+L(e.Stage)))+(startup.Error==null?"":"\n"+startup.Error);
        }
        if(startup==null && h.Id!=null && session?.IsAdmin==true)
        {
            HostLivePanel.Visibility=Visibility.Visible;HostLiveActivity.Visibility=Visibility.Collapsed;
            HostLivePhase.Text=L(h.Verified && h.Phase=="ready"?"hostReady":"hostConnectionLost");
            HostLiveInfo.Text=L("hostLiveInfo",h.Id,h.Ip??"…",r==null?"…":r.State+" / "+r.ProviderStatus)+"\n"+string.Join(" · ",new[]{r?.Model,r?.Memory,r?.Storage,r?.Location}.Where(v=>!string.IsNullOrWhiteSpace(v)));
            HostLiveJournal.Text="";HostLiveTiming.Text=r==null?"":r.CheckedAt.ToLocalTime().ToString("g");
        }
        RentalDescription.Text=r==null?L("rentalNoData"):(h.Id==null ? L("lastRental")+"\n" : "")+string.Join(" · ",new[]{r.Model,r.Cores is {} cores ? L("rentalCores",cores) : null,r.Memory,r.Storage,r.Location}.Where(s=>!string.IsNullOrWhiteSpace(s))) + "\n"+L("rentalId",r.Id)+(h.Id!=null && h.Ip!=null?" · "+h.Ip:"");
        var capacity=h.Id==null?0:pool.CpuCapacity;
        RentalCapacity.Text=h.Id==null?L("rentalSlotsUnavailable"):L("rentalSlots",occupied,Math.Max(0,capacity-occupied),capacity)+"\n"+L("rentalSlotHelp");
        string Money(decimal? amount,string? currency)=>amount is {} n?n.ToString("0.000")+" "+(currency??"?"):L("rentalUnknown");
        var activeRate=h.Id==null?0:r?.HourlyRate;
        var cost=r?.Estimate(now);
        RentalCosts.Text=L("rentalRate",Money(activeRate,r?.Currency));
        if(r!=null)
        {
            RentalCosts.Text+="\n"+L("rentalQuotedRate",Money(r.HourlyRate,r.Currency));
            RentalCosts.Text+="\n"+L("rentalEstimate",Money(cost,r.Currency));
            RentalCosts.Text+="\n"+L("rentalProviderAmount",Money(r.ProviderAmount,r.Currency),r.CheckedAt.ToLocalTime().ToString("g"));
            if(r.StartedAt is {} started)RentalCosts.Text+="\n"+L("rentalPeriod",started.ToLocalTime().ToString("g"),r.EndedAt?.ToLocalTime().ToString("g")??L("rentalOngoing"));
            if(r.EndTimeEstimated)RentalCosts.Text+="\n"+L("rentalEndEstimated");
        }
        var history=pool.RentalHistory??[];
        RentalTotals.Text=L("rentalTotals",string.Join(" · ",history.GroupBy(r=>r.Currency).Select(g=>g.Any(r=>r.Estimate(now)==null)?(g.Key??"?")+": "+L("rentalIncomplete"):Money(g.Sum(r=>r.Estimate(now)??0),g.Key))));
        if(history.Count==0)RentalTotals.Text=L("rentalNoHistory");
        RentalHistoryText.Text=string.Join("\n\n",history.OrderByDescending(r=>r.StartedAt).Select(r=>r.Id+" · "+r.Model+" · "+r.Location+"\n"+L(r.EndedAt==null?"rentalOngoing":"hostCancelled")+" · "+L("rentalEstimate",Money(r.Estimate(now),r.Currency))));
        if(h.Id==null)RenterStatus.Text="";
        RenderOffer();
    }
    private void RenderOffer()
    {
        RentHostButton.IsEnabled=pool != null && pool.Host.Id==null && pool.CanRent && pool.CanStart && !pool.Busy && RentalCities.SelectedItem is LocationRow { Value.Available: > 0, Value.HourlyPrice: not null, Value.PrebuiltId: not null };
        if(RentalCities.SelectedItem is not LocationRow row){RentalOffer.Text=L(RentalCities.ItemsSource!=null && RentalCities.Items.Count==0?"locationUnavailable":"offerChooseCity");RentalOfferPrice.Text="—";RentalOfferAvailability.Text="";return;}
        var r=row.Value;
        RentalOfferPrice.Text=r.HourlyPrice is {} rate?rate.ToString("0.000")+" "+r.Currency+" / "+L("hourUnit"):"—";
        RentalOfferAvailability.Text=r.Available>0?L("locationAvailable",r.Available):L("locationUnavailable");
        RentalOffer.Text="";
        if(r.PrebuiltId is not null)
        {
            RentalOffer.Text=L("offerCpu",r.Cores,r.Threads)+" · "+L("offerMemory",r.Memory);
            RentalOffer.Text+="\n"+L("offerStorage",r.Storage)+"\n"+L("offerNetwork",r.Network,r.Traffic);
        }
        if(r.HourlyPrice is {} price)RentalOffer.Text+="\n"+(price*24).ToString("0.00")+" "+r.Currency+" / "+L("day24Unit");
        else RentalOffer.Text+="\n"+L("offerNoPrice");
    }
    private async void RentHostClick(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        if(RentalCities.SelectedItem is not LocationRow row || row.Value.HourlyPrice==null || row.Value.PrebuiltId==null)throw new InputException(L("offerChooseCity"));
        var offer=row.Value;
        if(!Confirm(L("confirmRentHost",offer.Name,offer.HourlyPrice.Value.ToString("0.000"),offer.Currency),L("rentHost")))return;
        await Post<OperationView>("api/rent",new RentHostRequest(Guid.NewGuid(),offer.Slug,offer.HourlyPrice.Value,offer.Currency!,offer.PrebuiltId.Value));
        await Refresh();
    });
    private sealed record LocationRow(RentalLocation Value)
    {
        public string Display => Value.Name+" · "+L("locationAvailable",Value.Available);
    }
    private bool loadingCities;
    private async void LocationsRefreshClick(object sender,RoutedEventArgs e)
    {
        if(busy)return;
        var button=sender as Button;var icon=button?.Content as FrameworkElement;
        var rotation=new System.Windows.Media.RotateTransform();
        if(button!=null)button.IsEnabled=false;
        if(icon!=null){icon.RenderTransformOrigin=new Point(.5,.5);icon.RenderTransform=rotation;rotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,new System.Windows.Media.Animation.DoubleAnimation(0,360,TimeSpan.FromSeconds(.9)){RepeatBehavior=System.Windows.Media.Animation.RepeatBehavior.Forever});}
        try{await Safe(LoadLocations,false);}
        finally{rotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,null);rotation.Angle=0;if(button!=null)button.IsEnabled=true;}
    }
    private async Task LoadLocations()
    {
        using var response=await http!.GetAsync("api/locations");await Check(response);
        var locations=await response.Content.ReadFromJsonAsync<List<RentalLocation>>()??[];
        RestoreLocationWatches();currentLocations=locations;
        var choices=locations.Where(r=>r.Available>0).Select(r=>new LocationRow(r)).ToList();
        using var selectedResponse=await http.GetAsync("api/location");await Check(selectedResponse);var selected=await selectedResponse.Content.ReadFromJsonAsync<RentalLocationRequest>();
        loadingCities=true;
        try { RentalCities.ItemsSource=choices;RentalCities.SelectedItem=choices.FirstOrDefault(r=>r.Value.Slug==selected?.Region); }
        finally { loadingCities=false; RenderOffer(); }
        locationsChecked=DateTimeOffset.UtcNow;UpdateLocationBell();NotifyLocations(locationWatch.Observe(locations));
    }
    private async void RentalCitySelected(object sender,SelectionChangedEventArgs e)
    {
        RenderOffer();
        if(loadingCities || RentalCities.SelectedItem is not LocationRow row)return;
        await Safe(async()=>{using var response=await http!.PostAsJsonAsync("api/location",new RentalLocationRequest(row.Value.Slug));await Check(response);Status.Text=L("selectedRentalCity",row.Value.Name);});
    }

}
