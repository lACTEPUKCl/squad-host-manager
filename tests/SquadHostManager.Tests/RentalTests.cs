using System.Text.Json;
using SquadHostManager.Core;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;
public class RentalTests
{
    [Fact] public void CherryActualRateIsNotCatalogPriceOrRoundedTotal()
    {
        using var json=JsonDocument.Parse("""
        {"id":123,"created_at":"2026-10-01T00:00:00Z","state":"powered-on","plan":{"title":"Ryzen 9950X","pricing":[{"price":0.598}],"specs":{"cpus":{"cores":16},"memory":{"total":192,"unit":"GB"},"storage":[{"count":2,"name":"NVMe 1TB"}]}},"region":{"name":"Stockholm, SE"},"pricing":{"unit":"Hours","unit_price":0.67,"currency":"EUR","quantity":2,"price_total":1.33,"billable_amount":1.33}}
        """);
        var r=RentalRecord.FromCherry(json.RootElement,DateTimeOffset.UtcNow);Assert.Equal(.67m,r.HourlyRate);Assert.Equal(1.33m,r.ProviderAmount);Assert.Equal("Stockholm, SE",r.Location);Assert.Equal("192 GB",r.Memory);Assert.Equal(16,r.Cores);
    }
    [Fact] public void FixedTermAndUnknownPricesAreNotConvertedIntoHourlyCharges()
    {
        using var json=JsonDocument.Parse("""{"id":1,"pricing":{"unit":"Months","unit_price":100,"currency":"EUR"}}""");
        var r=RentalRecord.FromCherry(json.RootElement,DateTimeOffset.UtcNow);Assert.Null(r.HourlyRate);Assert.Null(r.Estimate(DateTimeOffset.UtcNow));
    }
    [Fact] public void RateChangesIntegrateAndEndedRentalNeverKeepsGrowing()
    {
        var start=DateTimeOffset.Parse("2026-10-01T00:00Z");var r=new RentalRecord{StartedAt=start,Rates=[new(start,1m),new(start.AddHours(2),2m)]};
        Assert.Equal(4m,r.Estimate(start.AddHours(3)));r.EndedAt=start.AddHours(3);Assert.Equal(4m,r.Estimate(start.AddYears(1)));
    }
    [Fact] public async Task OfflineProviderDisablesStartAndAutomaticallyRecovers()
    {
        using var f=new Fixture();f.Start(0);await f.Drain(1);f.Infra.Host=new("123","192.0.2.1",false);
        await Assert.ThrowsAsync<IOException>(()=>f.Pool.Reconcile(default));var down=f.Pool.Snapshot(Fixture.User(0));Assert.Equal("unavailable",down.Host.Phase);Assert.False(down.CanStart);Assert.False(down.Host.Verified);
        f.Infra.Host=new("123","192.0.2.1",true);await f.Pool.Reconcile(default);var up=f.Pool.Snapshot(Fixture.User(0));Assert.Equal("ready",up.Host.Phase);Assert.True(up.Host.Verified);Assert.True(up.CanStart);
    }
    [Fact] public async Task ExternalCancellationRetainsHistoryAcrossRestartWithoutRenting()
    {
        using var f=new Fixture();f.Start(0);await f.Drain(1);var start=DateTimeOffset.UtcNow.AddHours(-2);
        f.Infra.Host=new("123","192.0.2.1",true,new RentalRecord{Id="123",StartedAt=start,CheckedAt=DateTimeOffset.UtcNow,HourlyRate=.67m,Currency="EUR",Model="Ryzen"});await f.Pool.Reconcile(default);
        f.Infra.Host=null;await f.Pool.Reconcile(default);var restored=new PoolCoordinator(f.Store,f.Infra);var view=restored.Snapshot(new("observer","observer",true));Assert.Null(view.Host.Id);Assert.Single(view.RentalHistory!);Assert.NotNull(view.RentalHistory![0].EndedAt);Assert.True(view.RentalHistory[0].EndTimeEstimated);Assert.Equal("cancelled",view.Host.Rental!.State);Assert.Equal(1,f.Infra.Rents);Assert.Equal(0,f.Infra.Deletes);
    }
}
