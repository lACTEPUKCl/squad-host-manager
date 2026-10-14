using System.Text.RegularExpressions;
using SquadHostManager.Core;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public class CommonConfigurationTests
{
    [Fact] public async Task DownloadedDefaultsRefreshWithoutReplacingUserEdits()
    {
        using var f=new Fixture();f.Infra.StockConfigs["MOTD.cfg"]="New downloaded greeting";
        f.Pool.DefaultConfig(Fixture.User(1),new("MOTD.cfg","User greeting"));f.Start(1);f.Start(2);await f.Drain(2);
        var restored=new PoolCoordinator(f.Store,f.Infra);
        Assert.Equal("User greeting",restored.DefaultConfig(Fixture.User(1),new("MOTD.cfg")));
        Assert.Equal("New downloaded greeting",restored.DefaultConfig(Fixture.User(2),new("MOTD.cfg")));
    }
    [Fact] public void StockFilesExistBeforeRentalWithoutUserData()
    {
        using var f=new Fixture();var owner=Fixture.User(1);
        Assert.Equal(RemoteDocker.ConfigFiles.Order(),StandardConfigs.Names.Order());
        foreach(var file in RemoteDocker.ConfigFiles)Assert.Equal(StandardConfigs.Get(file),f.Pool.DefaultConfig(owner,new(file)));
        Assert.DoesNotContain("Admin=",Regex.Replace(StandardConfigs.Get("Admins.cfg"),@"(?m)^\s*//[^\r\n]*", ""));
        Assert.Null(SquadConfig.Get(f.Pool.DefaultConfig(owner,new("Server.cfg")),"ServerName"));
        Assert.Null(SquadConfig.Get(f.Pool.DefaultConfig(owner,new("Server.cfg")),"ServerPassword"));
        Assert.Equal(0,f.Infra.Rents);
    }
    [Fact] public async Task CommonSettingsReachAllOwnedServersButKeepEachIdentity()
    {
        using var f=new Fixture();var owner=Fixture.User(1);
        f.Pool.Start(owner,new(Guid.NewGuid(),"First server","first-pass",""));f.Pool.Start(owner,new(Guid.NewGuid(),"Second server","second-pass",""));f.Start(2);await f.Drain(3);
        var mine=f.Pool.Snapshot(owner).Instances.Where(i=>i.IsMine).ToArray();
        var common=SquadConfig.Set(StandardConfigs.Get("Server.cfg"),"RecordDemos","true");
        f.Pool.DefaultConfig(owner,new("Server.cfg",SquadConfig.Identity(common,"Ignored name","ignored-pass",80)));
        f.Pool.DefaultConfig(owner,new("MOTD.cfg","Shared welcome"));
        var restored=new PoolCoordinator(f.Store,f.Infra);
        foreach(var server in mine){restored.Restart(owner,server.Id,new(Guid.NewGuid()));await restored.ProcessNext(default);var stored=f.Infra.Configurations[(server.Id,"Server.cfg")];Assert.Equal(server.Name,SquadConfig.Get(stored,"ServerName"));Assert.Equal(server.Password,SquadConfig.Get(stored,"ServerPassword"));Assert.Equal("true",SquadConfig.Get(stored,"RecordDemos"));Assert.Equal("80",SquadConfig.Get(stored,"MaxPlayers"));Assert.Equal("Shared welcome",f.Infra.Configurations[(server.Id,"MOTD.cfg")]);}
        Assert.Equal(StandardConfigs.Get("MOTD.cfg"),restored.DefaultConfig(Fixture.User(2),new("MOTD.cfg")));
        Assert.Null(SquadConfig.Get(restored.DefaultConfig(owner,new("Server.cfg")),"ServerPassword"));
    }
    [Fact] public void LaunchPreparesSharedFilesBeforeStartingTheContainer()
    {
        var server=new Instance{CpuSet="1,17",Name="Test server",MaxPlayers=100,CommonConfigs=new(){["MOTD.cfg"]="Welcome",["Rcon.cfg"]="unsafe"}};
        var script=RemoteDocker.BuildStartScript(server);
        Assert.True(script.IndexOf("$dir/config/MOTD.cfg",StringComparison.Ordinal)<script.IndexOf("docker run -d",StringComparison.Ordinal));
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("unsafe")),script);
    }
}
