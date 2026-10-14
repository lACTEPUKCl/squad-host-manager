using SquadHostManager.Core;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public class CommonConfigHistoryTests
{
    [Fact] public void PreviousVersionsPersistAndStayPrivate()
    {
        using var f=new Fixture();var owner=Fixture.User(1);
        f.Pool.DefaultConfig(owner,new("MOTD.cfg","First"));f.Pool.DefaultConfig(owner,new("MOTD.cfg","Second"));
        var restored=new PoolCoordinator(f.Store,f.Infra);var versions=restored.CommonConfigHistory(owner,"MOTD.cfg");
        Assert.Equal("First",versions[0].Content);Assert.Empty(restored.CommonConfigHistory(Fixture.User(2),"MOTD.cfg"));
        restored.DefaultConfig(owner,new("MOTD.cfg",versions[0].Content));Assert.Equal("First",restored.DefaultConfig(owner,new("MOTD.cfg")));Assert.Equal("Second",restored.CommonConfigHistory(owner,"MOTD.cfg")[0].Content);
    }
    [Fact] public void ResetDoesNotDestroyTheEditedVersionOrStoreServerIdentity()
    {
        using var f=new Fixture();var owner=Fixture.User(1);
        f.Pool.DefaultConfig(owner,new("Server.cfg",SquadConfig.Identity(SquadConfig.Set(StandardConfigs.Get("Server.cfg"),"RecordDemos","true"),"Personal name","personal-secret",100)));
        f.Pool.DefaultConfig(owner,new("Server.cfg",f.Pool.StandardConfig(owner,"Server.cfg")));
        var previous=f.Pool.CommonConfigHistory(owner,"Server.cfg")[0].Content;Assert.Equal("true",SquadConfig.Get(previous,"RecordDemos"));Assert.Null(SquadConfig.Get(previous,"ServerName"));Assert.Null(SquadConfig.Get(previous,"ServerPassword"));
        Assert.Equal(StandardConfigs.Get("Server.cfg"),f.Pool.DefaultConfig(owner,new("Server.cfg")));
    }
    [Fact] public void HistoryIsBoundedAndUnchangedSavesDoNotAddVersions()
    {
        using var f=new Fixture();var owner=Fixture.User(1);for(var i=0;i<45;i++)f.Pool.DefaultConfig(owner,new("MOTD.cfg",i.ToString()));
        Assert.Equal(40,f.Pool.CommonConfigHistory(owner,"MOTD.cfg").Count);
        f.Pool.DefaultConfig(owner,new("MOTD.cfg","44"));Assert.Equal(40,f.Pool.CommonConfigHistory(owner,"MOTD.cfg").Count);
        Assert.Throws<ArgumentException>(()=>f.Pool.StandardConfig(owner,"../Rcon.cfg"));
    }
}
