using SquadHostManager.Core;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;

public class HostingPoolsTests
{
    [Fact] public void RetryingAfterProviderSelectionReusesOriginalRequestWithoutAnotherServer()
    {
        using var f=new Fixture();var pools=new HostingPools(f.Store,_=>f.Infra);var user=Fixture.User(1);
        var request=new StartRequest(Guid.NewGuid(),"Original server","","",20);
        var original=pools.Request(user,request.RequestId).Start(user,request);
        pools.Select(user,"respawn");
        var retry=pools.Request(user,request.RequestId).Start(user,request);
        Assert.Equal(original.Id,retry.Id);Assert.Empty(pools.For(user).Snapshot(user).Instances);
        Assert.Single(pools.Get("cherry").Snapshot(user).Instances);
        // A request may have resolved its provider before the first enqueue committed.
        Assert.Equal(original.Id,pools.Get("respawn").Start(user,request).Id);
        Assert.Empty(pools.Get("respawn").Snapshot(user).Instances);
    }
    [Fact] public async Task MembersChooseIndependentlyAndBothPoolsKeepWorkingAfterSelectionAndRestart()
    {
        using var f=new Fixture();var cherry=new FakeInfrastructure();var respawn=new FakeInfrastructure();
        var pools=new HostingPools(f.Store,p=>p=="cherry"?cherry:respawn);
        var fear=Fixture.User(1);var owner=Fixture.User(2);
        f.Store.Save("respawn-plan-"+fear.Id,new RespawnAllocation(1070,8192,.13m,"eu"));
        pools.Select(fear,"respawn");pools.For(fear).Start(fear,new(Guid.NewGuid(),"Fear server","","",20));await pools.For(fear).ProcessNext(default);
        var fearId=pools.For(fear).Snapshot(fear).Instances.Single().Id;
        Assert.True(pools.View(owner).CanSwitch);Assert.Equal("cherry",pools.Selected(owner));
        pools.For(owner).Start(owner,new(Guid.NewGuid(),"Cherry server","","",20));await pools.For(owner).ProcessNext(default);
        var cherryId=pools.For(owner).Snapshot(owner).Instances.Single().Id;
        pools.Select(fear,"cherry");
        Assert.Equal("running",pools.Resource(fear,fearId).Snapshot(fear).Instances.Single().Phase);
        Assert.Equal("response",await pools.Resource(fear,fearId).Access(fear,fearId,"ListPlayers",default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>pools.Resource(owner,fearId).Access(owner,fearId,"ListPlayers",default));
        pools.Select(owner,"respawn");Assert.Equal("cherry",pools.Selected(fear));
        Assert.Equal("running",pools.Resource(owner,cherryId).Snapshot(owner).Instances.Single().Phase);
        Assert.Equal(0,cherry.Stops+respawn.Stops+cherry.Deletes+respawn.Deletes);
        var restored=new HostingPools(f.Store,p=>p=="cherry"?cherry:respawn);
        Assert.Equal("respawn",restored.Selected(owner));Assert.Equal("cherry",restored.Selected(fear));
        Assert.Equal("running",restored.Resource(fear,fearId).Snapshot(fear).Instances.Single().Phase);
        Assert.Equal("running",restored.Resource(owner,cherryId).Snapshot(owner).Instances.Single().Phase);
    }
    [Theory][InlineData("running")][InlineData("error")]
    public async Task ConfirmedOfflineRespawnIsStoppedAndNeverAutomaticallyRestarted(string phase)
    {
        using var f=new Fixture();var instance=new Instance{Id="offline",OwnerId="user1",OwnerName="User 1",Name="Offline server",Phase=phase,Error="Old archive error",RecoveryNeeded=true};
        f.Store.Save("pool",new PoolState{Provider="respawn",HostId="respawn:test",Ip="respawnhost.com",HostPhase="ready",Instances=[instance]});
        f.Infra.Host=new("respawn:test","respawnhost.com",true);f.Infra.Containers.Add(new("offline",false,[]));
        var pool=new PoolCoordinator(f.Store,f.Infra);await pool.Reconcile(default);await pool.TickFeatures(default);await pool.ProcessNext(default);
        var view=Assert.Single(pool.Snapshot(Fixture.User(1)).Instances);
        Assert.Equal("stopped",view.Phase);Assert.Null(view.Error);Assert.Null(view.GameState);
        Assert.Single(pool.Tools(Fixture.User(1),null).Matches);Assert.Equal(0,f.Infra.Starts+f.Infra.Stops+f.Infra.Deletes);
    }
    [Fact] public async Task InventoryFailureDoesNotPretendRespawnStopped()
    {
        using var f=new Fixture();f.Store.Save("pool",new PoolState{Provider="respawn",HostId="respawn:test",Ip="respawnhost.com",HostPhase="ready",Instances=[new(){Id="unknown",OwnerId="user1",Name="Unknown",Phase="error"}]});
        f.Infra.Host=new("respawn:test","respawnhost.com",true);f.Infra.FailInventory=true;
        var pool=new PoolCoordinator(f.Store,f.Infra);await Assert.ThrowsAsync<IOException>(()=>pool.Reconcile(default));
        Assert.Equal("error",Assert.Single(pool.Snapshot(Fixture.User(1)).Instances).Phase);
    }
    [Fact] public async Task OfflineArchiveFailureDoesNotBlockSelectionOrCausePowerCallsAndArchiveIsRetried()
    {
        using var f=new Fixture();f.Store.Save("pool",new PoolState{Provider="respawn",HostId="respawn:test",Ip="respawnhost.com",HostPhase="ready",Instances=[new(){Id="offline",OwnerId="user1",Name="Offline",Phase="error",RecoveryNeeded=true}]});
        f.Infra.Host=new("respawn:test","respawnhost.com",true);f.Infra.Containers.Add(new("offline",false,[]));f.Infra.FailArchive=true;
        var pools=new HostingPools(f.Store,_=>f.Infra);var pool=pools.Get("respawn");await pool.Reconcile(default);
        Assert.Equal("stopped",Assert.Single(pool.Snapshot(Fixture.User(1)).Instances).Phase);
        pools.Select(Fixture.User(1),"cherry");Assert.Equal("cherry",pools.Selected(Fixture.User(1)));
        f.Infra.FailArchive=false;await pool.Reconcile(default);await pool.TickFeatures(default);await pool.ProcessNext(default);
        Assert.Null(Assert.Single(pool.Snapshot(Fixture.User(1)).Instances).Error);Assert.Single(pool.Tools(Fixture.User(1),null).Matches);
        Assert.Equal(0,f.Infra.Starts+f.Infra.Stops+f.Infra.Deletes);
    }
    [Fact] public void LegacyRespawnPoolAndSavedCherryStateArePreservedWithoutCopyingServersBetweenProviders()
    {
        using var f=new Fixture();
        f.Store.Save("pool",new PoolState{Provider="respawn",HostId="respawn:old",Instances=[new(){Id="fear",Phase="running",OwnerId="user1"}]});
        f.Store.Save("provider-state-cherry",new PoolState{HostId="old-cherry",Instances=[new(){Id="old",Phase="stopped",OwnerId="user2"}]});
        var pools=new HostingPools(f.Store,_=>f.Infra);pools.Select(Fixture.User(2),"cherry");
        Assert.Equal("respawn",pools.Selected(Fixture.User(1)));Assert.Equal("cherry",pools.Selected(Fixture.User(2)));
        Assert.Equal("fear",Assert.Single(pools.For(Fixture.User(1)).Snapshot(Fixture.User(1)).Instances).Id);
        Assert.Equal("old",Assert.Single(pools.For(Fixture.User(2)).Snapshot(Fixture.User(2)).Instances).Id);
    }
    [Fact] public void SwitchingDoesNotBypassTheUsersTotalServerLimit()
    {
        using var f=new Fixture();var user=Fixture.User(1);f.Store.Save("access-policy-"+user.Id,new AccessPolicy(null,1,true));
        var pools=new HostingPools(f.Store,_=>f.Infra);
        pools.For(user).Start(user,new(Guid.NewGuid(),"First server","","",20));
        pools.Select(user,"respawn");f.Store.Save("respawn-plan-"+user.Id,new RespawnAllocation(1070,8192,.13m,"eu"));
        Assert.Throws<InvalidOperationException>(()=>pools.For(user).Start(user,new(Guid.NewGuid(),"Second server","","",20)));
        Assert.Empty(pools.For(user).Snapshot(user).Instances);
    }
    [Fact] public async Task SharedAdminsApplyToOwnServersOnBothProvidersWithoutTouchingOtherUsers()
    {
        using var f=new Fixture();var cherry=new FakeInfrastructure();var respawn=new FakeInfrastructure();
        var pools=new HostingPools(f.Store,p=>p=="cherry"?cherry:respawn);var user=Fixture.User(1);var other=Fixture.User(2);
        pools.For(user).Start(user,new(Guid.NewGuid(),"Cherry own","","",20));await pools.For(user).ProcessNext(default);
        var cherryId=Assert.Single(pools.For(user).Snapshot(user).Instances).Id;
        pools.Select(user,"respawn");f.Store.Save("respawn-plan-"+user.Id,new RespawnAllocation(1070,8192,.13m,"eu"));
        pools.For(user).Start(user,new(Guid.NewGuid(),"Respawn own","","",20));await pools.For(user).ProcessNext(default);
        var respawnId=Assert.Single(pools.For(user).Snapshot(user).Instances).Id;
        pools.For(other).Start(other,new(Guid.NewGuid(),"Other user","","",20));await pools.For(other).ProcessNext(default);
        var result=await pools.SaveCommonConfig(user,new("Admins.cfg","Group=Owner:kick,ban"),default);
        Assert.Equal(2,result.Applied);Assert.Empty(result.Failed);
        Assert.Equal("Group=Owner:kick,ban",cherry.Configurations[(cherryId,"Admins.cfg")]);
        Assert.Equal("Group=Owner:kick,ban",respawn.Configurations[(respawnId,"Admins.cfg")]);
        Assert.All(cherry.InstanceCommands,c=>Assert.Equal(cherryId,c.Instance));
        Assert.All(respawn.InstanceCommands,c=>Assert.Equal(respawnId,c.Instance));
    }
}
