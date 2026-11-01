using SquadHostManager.Service;
namespace SquadHostManager.Tests;
public class HostingProviderTests
{
    private static readonly User Admin=new("admin","admin",true);
    [Fact] public async Task QueuedOrRunningServersBlockProviderChange()
    {
        using var f=new Fixture();f.Start(1);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Pool.SwitchProvider(Admin,"respawn",()=>Task.FromResult(false),default));
        await f.Drain(1);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Pool.SwitchProvider(Admin,"respawn",()=>Task.FromResult(false),default));
        Assert.Equal("cherry",f.Pool.Provider().Selected);
    }
    [Fact] public async Task StoppedServersAllowSwitchWithoutReleasingRentalAndBlockOldClientStarts()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);var id=f.Pool.Snapshot(Fixture.User(1)).Instances.Single().Id;
        f.Pool.Stop(Fixture.User(1),new(Guid.NewGuid(),false,id));await f.Drain(1);
        await f.Pool.SwitchProvider(Admin,"respawn",()=>Task.FromResult(false),default);
        Assert.Equal("respawn",new PoolCoordinator(f.Store,f.Infra).Provider().Selected);
        Assert.Equal(0,f.Infra.Deletes);Assert.Equal(1,f.Infra.Rents);
        Assert.Throws<InvalidOperationException>(()=>f.Start(2));
        Assert.Throws<InvalidOperationException>(()=>f.Pool.Restart(Fixture.User(1),id,new(Guid.NewGuid())));
    }
    [Theory][InlineData(true)][InlineData(false)] public async Task ExternalServerAndUnknownInventoryBlockSwitch(bool running)
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);var id=f.Pool.Snapshot(Fixture.User(1)).Instances.Single().Id;
        f.Pool.Stop(Fixture.User(1),new(Guid.NewGuid(),false,id));await f.Drain(1);
        f.Infra.Containers.Add(new("external",running,[]));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Pool.SwitchProvider(Admin,"respawn",()=>Task.FromResult(false),default));
        f.Infra.Containers.Clear();f.Infra.FailInventory=true;
        await Assert.ThrowsAsync<IOException>(()=>f.Pool.SwitchProvider(Admin,"respawn",()=>Task.FromResult(false),default));
        Assert.Equal("cherry",f.Pool.Provider().Selected);
    }
    [Fact] public async Task ReturningToCherryRestoresProfilesAndKeepsCommonConfigsAndArchives()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);var id=f.Pool.Snapshot(Fixture.User(1)).Instances.Single().Id;
        f.Pool.DefaultConfig(Fixture.User(1),new("Admins.cfg","Group=Owner:kick"));
        f.Pool.Stop(Fixture.User(1),new(Guid.NewGuid(),false,id));await f.Drain(1);
        await f.Pool.SwitchProvider(Admin,"respawn",()=>Task.FromResult(false),default);
        Assert.Empty(f.Pool.Snapshot(Fixture.User(1)).Instances);Assert.Single(f.Pool.Tools(Fixture.User(1),null).Matches);
        Assert.Contains("Owner",f.Pool.Defaults(Fixture.User(1)).Configs!["Admins.cfg"]);
        await f.Pool.SwitchProvider(Admin,"cherry",()=>Task.FromResult(false),default);
        Assert.Equal(id,f.Pool.Snapshot(Fixture.User(1)).Instances.Single().Id);
        await using var archive=await f.Pool.ArchiveStream(Fixture.User(1),id,default);
        Assert.Equal(0,f.Infra.Deletes);Assert.Equal(1,f.Infra.Rents);
    }
    [Fact] public async Task RespawnCannotAccidentallyAdoptOrConfigureACherryHost()
    {
        using var f=new Fixture();await f.Pool.SwitchProvider(Admin,"respawn",()=>Task.FromResult(false),default);
        f.Infra.Host=new("999","192.0.2.10",true);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Pool.Adopt(Admin,"999",default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Pool.SetRegion(Admin,"DE",default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Pool.Recover(Admin,true,default));
        Assert.Null(f.Pool.Snapshot(Admin).Host.Id);
    }
    [Fact] public async Task ReverseSwitchChecksRespawnAndOnlyAdminCanSwitch()
    {
        using var f=new Fixture();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Pool.SwitchProvider(Fixture.User(1),"respawn",()=>Task.FromResult(false),default));
        await f.Pool.SwitchProvider(Admin,"respawn",()=>Task.FromResult(false),default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Pool.SwitchProvider(Admin,"cherry",()=>Task.FromResult(true),default));
        Assert.Equal("respawn",f.Pool.Provider().Selected);
        await f.Pool.SwitchProvider(Admin,"cherry",()=>Task.FromResult(false),default);
        Assert.Equal("cherry",f.Pool.Provider().Selected);
    }
    [Fact] public async Task CherryStartCannotRaceTheRemoteInventoryCheck()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);var id=f.Pool.Snapshot(Fixture.User(1)).Instances.Single().Id;
        f.Pool.Stop(Fixture.User(1),new(Guid.NewGuid(),false,id));await f.Drain(1);
        f.Infra.InventoryEntered=new();f.Infra.InventoryBlock=new();
        var change=f.Pool.SwitchProvider(Admin,"respawn",()=>Task.FromResult(false),default);
        await f.Infra.InventoryEntered.Task;Assert.Throws<InvalidOperationException>(()=>f.Start(2));
        f.Infra.InventoryBlock.SetResult();await change;Assert.Equal("respawn",f.Pool.Provider().Selected);
    }
    [Fact] public async Task ConcurrentStartIsRejectedWhileProviderCheckIsPending()
    {
        using var f=new Fixture();await f.Pool.SwitchProvider(Admin,"respawn",()=>Task.FromResult(false),default);
        var entered=new TaskCompletionSource();var release=new TaskCompletionSource<bool>();
        var change=f.Pool.SwitchProvider(Admin,"cherry",async()=>{entered.SetResult();return await release.Task;},default);
        await entered.Task;Assert.Throws<InvalidOperationException>(()=>f.Start(1));
        release.SetResult(false);await change;f.Start(1);
    }
}
