using SquadHostManager.Core;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;
public class ServerDefaultsTests
{
    [Fact] public async Task ConfigCanBePreparedBeforeRentalAndSurvivesRestart()
    {
        using var f=new Fixture();var owner=Fixture.User(1);var admins="Group=Owner:chat,kick\nAdmin=76561198000000001:Owner\n";
        f.Pool.DefaultConfig(owner,new("Admins.cfg",admins));f.Pool.DefaultConfig(owner,new("MOTD.cfg","Welcome"));
        var restored=new PoolCoordinator(f.Store,f.Infra);
        Assert.Equal(admins,restored.DefaultConfig(owner,new("Admins.cfg")));Assert.Equal(StandardConfigs.Get("MOTD.cfg"),restored.DefaultConfig(Fixture.User(2),new("MOTD.cfg")));
        Assert.Throws<ArgumentException>(()=>restored.DefaultConfig(owner,new("../Server.cfg","bad")));
        Assert.Throws<ArgumentException>(()=>restored.DefaultConfig(owner,new("MOTD.cfg",new string('x',20001))));
        restored.Start(owner,new(Guid.NewGuid(),"Prepared","",""));await restored.ProcessNext(default);
        var server=restored.Snapshot(owner).Instances.Single();Assert.Equal("Welcome",f.Infra.Configurations[(server.Id,"MOTD.cfg")]);Assert.Equal(admins,f.Infra.Configurations[(server.Id,"Admins.cfg")]);
    }
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
