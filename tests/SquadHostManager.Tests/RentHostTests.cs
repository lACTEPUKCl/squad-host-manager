using SquadHostManager.Core;
using SquadHostManager.Service;
using Microsoft.Extensions.Configuration;
namespace SquadHostManager.Tests;
public class RentHostTests
{
    private static RentHostRequest Offer(Fixture f)
    {
        f.Infra.RentalLocations.Add(new("DE-Frankfurt","Frankfurt",1,.666m,"EUR",42));
        return new(Guid.NewGuid(),"DE-Frankfurt",.666m,"EUR",42);
    }
    [Fact] public async Task RentOnlyCreatesHostNotGameAndDuplicatesCannotOrderTwice()
    {
        using var f=new Fixture();var request=Offer(f);var op=await f.Pool.RentHost(Fixture.User(0),request,default);
        Assert.Equal(op,await f.Pool.RentHost(Fixture.User(0),request,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Pool.RentHost(Fixture.User(1),request with{RequestId=Guid.NewGuid()},default));
        await f.Drain(1);var view=f.Pool.Snapshot(Fixture.User(0));Assert.Equal(1,f.Infra.Rents);Assert.Empty(view.Instances);Assert.Equal("User 0",view.Host.RentedByName);Assert.Equal("ready",view.Host.Phase);
        f.Start(1);await f.Drain(1);Assert.Equal(1,f.Infra.Rents);Assert.Single(f.Pool.Snapshot(Fixture.User(1)).Instances);
    }
    [Fact] public async Task ChangedPriceOrUnavailableOfferCannotQueuePaidOrder()
    {
        using var f=new Fixture();var request=Offer(f);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Pool.RentHost(Fixture.User(0),request with{HourlyPrice=.1m},default));
        f.Infra.RentalLocations.Clear();await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Pool.RentHost(Fixture.User(0),request,default));Assert.Equal(0,f.Infra.Rents);Assert.False(f.Pool.Snapshot(Fixture.User(0)).Busy);
    }
    [Fact] public async Task DisableRentStillBlocksStandaloneRental()
    {
        using var f=new Fixture();var request=Offer(f);var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Cherry:DisableRent"]="true"}).Build();var pool=new PoolCoordinator(f.Store,f.Infra,config);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>pool.RentHost(Fixture.User(0),request,default));Assert.Equal(0,f.Infra.Rents);
    }
}
