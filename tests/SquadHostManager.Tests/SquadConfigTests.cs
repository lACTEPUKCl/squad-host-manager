using System.Text;
using System.Text.RegularExpressions;
using SquadHostManager.Core;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public class SquadConfigTests
{
    [Fact] public void UpdatingOneFieldPreservesCommentsAndUnknownModOptions()
    {
        var text="// keep\nMapRotationMode=LayerList\nCustomModSetting=abc\nMaxPlayers=40\nMaxPlayers=50\n";
        var updated=SquadConfig.Set(text,"MaxPlayers","80");
        Assert.Contains("// keep",updated);Assert.Contains("CustomModSetting=abc",updated);Assert.Equal("80",SquadConfig.Get(updated,"MaxPlayers"));Assert.Single(Regex.Matches(updated,"MaxPlayers=").Cast<Match>());
        Assert.Null(SquadConfig.Get(SquadConfig.Remove(updated,"MaxPlayers"),"MaxPlayers"));
    }
    [Theory][InlineData("MaxPlayers=101")][InlineData("RconPassword=secret")][InlineData("Port=21114")][InlineData("ServerName=x")]
    public void InvalidOrInfrastructureSettingsAreRejected(string text)=>Assert.Throws<ArgumentException>(()=>SquadConfig.Validate(text));
    [Fact] public void QuotedValuesAndInlineCommentsCanBeImported()
    {
        var text="ServerName=\"My // server\" // name\nMaxPlayers=80 // limit\n";
        SquadConfig.Validate(text);Assert.Equal("My // server",SquadConfig.Get(text,"ServerName"));Assert.Equal("80",SquadConfig.Get(text,"MaxPlayers"));
    }
    [Fact] public void StartupRetainsGameOptionsAndUsesCurrentIdentity()
    {
        var instance=new Instance{CpuSet="1,17",Name="Current name",Password="Current password",MaxPlayers=80,ServerConfig="// preserved\nServerName=Old\nMaxPlayers=40\nRecordDemos=true\nModRule=keep\n"};
        var script=RemoteDocker.BuildStartScript(instance);
        var cfg=Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Match(script,@"printf %s '([^']+)' \| base64 -d > ""\$dir/config/Server.cfg""").Groups[1].Value));
        Assert.Contains("RecordDemos=true",cfg);Assert.Contains("ModRule=keep",cfg);Assert.Contains("// preserved",cfg);Assert.Equal("Current name",SquadConfig.Get(cfg,"ServerName"));Assert.Equal("80",SquadConfig.Get(cfg,"MaxPlayers"));
    }
    [Fact] public async Task PreRentalMainConfigIsScopedAndRetainedAfterRestart()
    {
        using var f=new Fixture();var owner=Fixture.User(1);
        f.Pool.DefaultConfig(owner,new("Server.cfg",SquadConfig.Set(SquadConfig.Initial,"RecordDemos","true")));
        Assert.Equal(StandardConfigs.Get("Server.cfg"),f.Pool.DefaultConfig(Fixture.User(2),new("Server.cfg")));
        f.Start(1);await f.Drain(1);var id=f.Pool.Snapshot(owner).Instances.Single().Id;
        var content=SquadConfig.Set(SquadConfig.Initial,"PublicQueueLimit","25");await f.Pool.Config(owner,id,new("Server.cfg",content),default);
        var restored=new PoolCoordinator(f.Store,f.Infra);Assert.Equal("25",SquadConfig.Get(await restored.Config(owner,id,new("Server.cfg"),default),"PublicQueueLimit"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>restored.Config(Fixture.User(2),id,new("Server.cfg",content),default));
    }
}
