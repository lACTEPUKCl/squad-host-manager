using SquadHostManager.Core;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public class LiveAdminsTests
{
    [Fact] public async Task SavesAndReloadsEveryOwnedRunningServerWithoutRestart()
    {
        using var f=new Fixture();var owner=Fixture.User(0);
        f.Start(0);f.Start(0);f.Start(1);await f.Drain(3);
        var mine=f.Pool.Snapshot(owner).Instances.Where(i=>i.IsMine).ToArray();
        var other=f.Pool.Snapshot(Fixture.User(1)).Instances.Single();
        var previous=f.Infra.Configurations[(other.Id,"Admins.cfg")];
        var result=await f.Pool.SaveCommonConfig(owner,new("Admins.cfg","Group=Moderator:kick,ban\nAdmin=123:Moderator"),default);
        Assert.Equal(2,result.Applied);Assert.Empty(result.Failed);
        Assert.Equal(mine.Select(i=>i.Id).Order(),f.Infra.InstanceCommands.Select(i=>i.Instance).Order());
        Assert.All(f.Infra.InstanceCommands,i=>Assert.Equal("AdminReloadServerConfig",i.Command));
        Assert.All(mine,i=>Assert.Equal(result.Output,f.Infra.Configurations[(i.Id,"Admins.cfg")]));
        Assert.Equal(previous,f.Infra.Configurations[(other.Id,"Admins.cfg")]);
        Assert.Equal(3,f.Infra.Starts);Assert.Equal(0,f.Infra.Stops);Assert.Equal(0,f.Infra.Deletes);Assert.Equal(1,f.Infra.Rents);
    }
    [Fact] public async Task FailedReloadIsDurableAndRetriesAfterCoordinatorRestart()
    {
        using var f=new Fixture();f.Start(0);await f.Drain(1);var owner=Fixture.User(0);
        var id=f.Pool.Snapshot(owner).Instances.Single().Id;f.Infra.FailCommandsFor.Add(id);
        var result=await f.Pool.SaveCommonConfig(owner,new("Admins.cfg","Group=Moderator:kick"),default);
        Assert.Single(result.Failed);Assert.Equal(0,result.Applied);
        var restored=new PoolCoordinator(f.Store,f.Infra);
        Assert.Equal(result.Output,restored.DefaultConfig(owner,new("Admins.cfg")));
        f.Infra.FailCommandsFor.Clear();f.Infra.GameStatus=new("gameReady");
        await restored.Reconcile(default);Assert.Equal(2,f.Infra.Commands.Count);
        await restored.Reconcile(default);Assert.Equal(2,f.Infra.Commands.Count);
        Assert.Equal(1,f.Infra.Starts);Assert.Equal(0,f.Infra.Stops);
    }
    [Fact] public async Task OfflineDefaultsAndOtherFilesDoNotSendReload()
    {
        using var f=new Fixture();var owner=Fixture.User(0);
        var offline=await f.Pool.SaveCommonConfig(owner,new("Admins.cfg","Group=Moderator:kick"),default);
        Assert.Equal(0,offline.Applied);Assert.Empty(offline.Failed);
        f.Start(0);await f.Drain(1);
        await f.Pool.SaveCommonConfig(owner,new("MOTD.cfg","Welcome"),default);
        await f.Pool.SaveCommonConfig(owner,new("Admins.cfg"),default);
        Assert.Empty(f.Infra.Commands);
    }
}
