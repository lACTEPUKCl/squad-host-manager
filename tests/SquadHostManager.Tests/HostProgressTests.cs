using SquadHostManager.Core;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;
public class HostProgressTests
{
    [Fact] public async Task CapacityIsKnownBeforeFirstGameServerIsOrdered()
    {
        using var f=new Fixture();f.Infra.CpuLayout=["1,17","2,18","3,19"];
        await Queue(f);await f.Drain(1);
        Assert.Equal(3,f.Pool.Snapshot(Fixture.User(0)).CpuCapacity);
        Assert.Equal(0,f.Infra.Starts);
        f.Start(0);f.Start(1);f.Start(2);
        Assert.Throws<InvalidOperationException>(()=>f.Start(3));
    }
    private static async Task Queue(Fixture f)
    {
        f.Infra.RentalLocations.Add(new("DE-Frankfurt","Frankfurt",1,.666m,"EUR",42));
        await f.Pool.RentHost(Fixture.User(0),new(Guid.NewGuid(),"DE-Frankfurt",.666m,"EUR",42),default);
    }
    [Fact] public async Task SlowPreparationPublishesStagesAndFreshProviderDataBeforeItCompletes()
    {
        using var f=new Fixture();f.Infra.PrepareEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);f.Infra.PrepareBlock=new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Queue(f);var processing=f.Pool.ProcessNext(default);await f.Infra.PrepareEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var pending=f.Pool.Snapshot(Fixture.User(0));Assert.True(pending.Busy);Assert.Equal("imageDownloading",pending.Host.Startup!.Stage);Assert.Contains(pending.Host.Startup.Events,e=>e.Stage=="sshConnected");Assert.False(processing.IsCompleted);
            f.Infra.Host=new("123","192.0.2.1",true,new RentalRecord{Id="123",Model="Ryzen 9950X",State="active",ProviderStatus="deployed",Memory="192 GB",CheckedAt=DateTimeOffset.UtcNow,HourlyRate=.75m,Currency="EUR"});
            var deadline=DateTimeOffset.UtcNow.AddSeconds(8);while(f.Pool.Snapshot(Fixture.User(0)).Host.Rental?.HourlyRate!=.75m && DateTimeOffset.UtcNow<deadline)await Task.Delay(100);
            var updated=f.Pool.Snapshot(Fixture.User(0));Assert.Equal(.75m,updated.Host.Rental!.HourlyRate);Assert.Equal("192 GB",updated.Host.Rental.Memory);Assert.True(updated.Busy);Assert.False(processing.IsCompleted);
        }
        finally{f.Infra.PrepareBlock.TrySetResult();await processing;}
        var ready=f.Pool.Snapshot(Fixture.User(0));Assert.Equal("hostReady",ready.Host.Startup!.Stage);Assert.False(ready.Busy);Assert.Equal(1,f.Infra.Rents);
    }
    [Fact] public async Task FailedPreparationExposesErrorAndKeepsExistingRental()
    {
        using var f=new Fixture();f.Infra.FailPrepare=true;await Queue(f);await f.Drain(1);var view=f.Pool.Snapshot(new("observer","observer",true));
        Assert.Equal("hostFailed",view.Host.Startup!.Stage);Assert.Equal("Preparation failed",view.Host.Startup.Error);Assert.NotNull(view.Host.Id);Assert.Equal(1,f.Infra.Rents);Assert.Equal(0,f.Infra.Deletes);
        var restored=new PoolCoordinator(f.Store,f.Infra);Assert.Equal("hostFailed",restored.Snapshot(Fixture.User(0)).Host.Startup!.Stage);
    }
}
