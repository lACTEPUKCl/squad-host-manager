using SquadHostManager.Core;
namespace SquadHostManager.Service;
public record RespawnMeter(string Id,string InstanceId,string OwnerId,string OwnerName,DateTimeOffset StartedAt,DateTimeOffset? EndedAt,decimal Rate);
public static class RespawnMetering
{
    public static void Begin(Store store,Instance instance,decimal rate)
    {
        var meters=store.Load<List<RespawnMeter>>("respawn-meters")??[];
        if(meters.Any(m=>m.InstanceId==instance.Id && m.EndedAt==null))return;
        meters.Add(new("respawn-meter:"+Guid.NewGuid().ToString("N"),instance.Id,instance.OwnerId,instance.OwnerName,DateTimeOffset.UtcNow,null,rate));store.Save("respawn-meters",meters);
    }
    public static void End(Store store,string id)
    {
        var meters=store.Load<List<RespawnMeter>>("respawn-meters")??[];
        for(var i=0;i<meters.Count;i++)if(meters[i].InstanceId==id && meters[i].EndedAt==null)meters[i]=meters[i] with{EndedAt=DateTimeOffset.UtcNow};
        store.Save("respawn-meters",meters);
    }
}
public sealed partial class PoolCoordinator
{
    private void RefreshManagedCosts()
    {
        foreach(var meter in store.Load<List<RespawnMeter>>("respawn-meters")??[])
        {
            var record=new RentalRecord{Id=meter.Id,Model="RespawnHost Squad",Currency="EUR",BillingUnit="seconds",HourlyRate=meter.Rate,StartedAt=meter.StartedAt,EndedAt=meter.EndedAt,CheckedAt=DateTimeOffset.UtcNow,State=meter.EndedAt==null?"running":"stopped",EstimateIncomplete=true,Rates=[new(meter.StartedAt,meter.Rate)]};
            state.Rentals.RemoveAll(r=>r.Id==meter.Id);state.Rentals.Add(record);
            if(!state.CostEvents.Any(e=>e.HostId==meter.Id))state.CostEvents.Add(new(meter.StartedAt,meter.Id,[new(meter.OwnerId,meter.OwnerName)]));
        }
    }
}
