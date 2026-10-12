using SquadHostManager.Core;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;
public class ServerDefaultsTests
{
    [Fact] public async Task DefaultsPersistAndApplyAdminsOnlyToOwnersNewServers()
    {
        using var f=new Fixture();f.Pool.Start(Fixture.User(1),new(Guid.NewGuid(),"First","","3816653940"));await f.Drain(1);
        var first=f.Pool.Snapshot(Fixture.User(1)).Instances.Single();var admins="Group=Owner:chat,kick\nAdmin=76561198000000001:Owner\n";
        await f.Pool.Config(Fixture.User(1),first.Id,new("Admins.cfg",admins),default);
        var restored=new PoolCoordinator(f.Store,f.Infra);Assert.Equal("3816653940",restored.Defaults(Fixture.User(1)).Mods);Assert.Equal(admins,restored.Defaults(Fixture.User(1)).Admins);Assert.Null(restored.Defaults(Fixture.User(2)).Admins);
        restored.Start(Fixture.User(1),new(Guid.NewGuid(),"Second","","3816653940"));await restored.ProcessNext(default);
        var second=restored.Snapshot(Fixture.User(1)).Instances.Single(i=>i.Name=="Second");Assert.Equal(admins,f.Infra.Configurations[(second.Id,"Admins.cfg")]);
        restored.Settings(Fixture.User(1),second.Id,new("Second",null,"",100));Assert.Equal("",restored.Defaults(Fixture.User(1)).Mods);Assert.Equal(admins,restored.Defaults(Fixture.User(1)).Admins);
    }
}
