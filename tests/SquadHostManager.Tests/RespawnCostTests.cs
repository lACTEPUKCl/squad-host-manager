using SquadHostManager.Core;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;
public class RespawnCostTests
{
    [Fact] public void RespawnSidebarExcludesCherryAndShowsOwnerTotalAndMemberShare()
    {
        using var f=new Fixture();var start=DateTimeOffset.UtcNow.AddMinutes(-10);var end=start.AddMinutes(10);
        var state=new PoolState{Provider="respawn",HostId="respawn:test",HostPhase="ready"};
        state.Rentals.Add(new(){Id="cherry-old",StartedAt=start,EndedAt=end,Currency="EUR",HourlyRate=10});
        state.CostEvents.Add(new(start,"cherry-old",[new("user1","User 1")]));
        for(var i=1;i<=2;i++)
        {
            var id="respawn-meter:"+i;state.Rentals.Add(new(){Id=id,StartedAt=start,EndedAt=end,Currency="EUR",BillingUnit="seconds",HourlyRate=0.12m});
            state.CostEvents.Add(new(start,id,[new("user"+i,"User "+i)]));
            var instance=new Instance{Id="instance"+i,OwnerId="user"+i,Phase="running"};state.Instances.Add(instance);
            f.Store.Save("respawn-instance-"+instance.Id,new RespawnManagedServer(Guid.NewGuid().ToString(),instance.OwnerId,PortSet.ForSlot(i),new(1070,8192,0.12m,"eu")));
        }
        f.Store.Save("pool",state);var pool=new PoolCoordinator(f.Store,f.Infra);
        var owner=pool.Features(new("admin","admin",true),null);Assert.Equal(0.04m,owner.HostSpent);Assert.Equal(0.24m,owner.HourlyForecast);
        var member=pool.Features(Fixture.User(1),null);Assert.Equal(0.02m,member.HostSpent);Assert.Equal(0.12m,member.HourlyForecast);Assert.Equal(0.02m,Assert.Single(member.Costs).Spent);
    }
    [Fact] public void PaygUsesActualSecondsRatherThanFullCherryHours()
    {
        var start=DateTimeOffset.UtcNow.AddMinutes(-10);var end=start.AddMinutes(10);
        var rental=new RentalRecord{Id="respawn",StartedAt=start,EndedAt=end,Currency="EUR",BillingUnit="seconds",HourlyRate=0.1332m,EstimateIncomplete=true};
        var report=CostAccounting.Calculate([rental],[new(start,"respawn",[new("owner","Owner")])],end);
        Assert.Equal(0.0222m,report.Users.Single().Spent,6);Assert.True(report.HistoryIncomplete);
        rental.BillingUnit="hours";Assert.Equal(0.1332m,CostAccounting.Calculate([rental],[new(start,"respawn",[new("owner","Owner")])],end).Users.Single().Spent);
    }
}
