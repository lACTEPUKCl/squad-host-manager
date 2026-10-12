using SquadHostManager.Core;
namespace SquadHostManager.Tests;
public class StartupMapTests
{
    [Theory]
    [InlineData("AlBasrah_AAS_v1")]
    [InlineData("/FlightSchool/Content/Maps/AlBasrah_FlightSchool_v1")]
    public async Task LayerOrEngineMapReferenceIsPreserved(string map)
    {
        using var f=new Fixture();f.Pool.Start(Fixture.User(0),new(Guid.NewGuid(),"QA Server","","",100,null,map));await f.Drain(1);
        var instance=f.Pool.Snapshot(Fixture.User(0)).Instances.Single();Assert.Equal(map,instance.StartLayer);
        f.Pool.Settings(Fixture.User(0),instance.Id,new("QA Server",null,"",100));
        Assert.Equal(map,f.Pool.Snapshot(Fixture.User(0)).Instances.Single().StartLayer);
    }
    [Theory]
    [InlineData("x; reboot")]
    [InlineData("/Game/../Config")]
    [InlineData("/Game//Maps/Test")]
    [InlineData("/Game/Maps/Test?listen")]
    public void NonAssetPathsAndCommandsAreRejected(string map)
    {
        using var f=new Fixture();Assert.Throws<ArgumentException>(()=>f.Pool.Start(Fixture.User(0),new(Guid.NewGuid(),"QA Server","","",100,null,map)));
    }
}
