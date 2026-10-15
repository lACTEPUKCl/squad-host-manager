using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public class InstallationRecoveryTests
{
    [Fact] public async Task AdminCanRetryOnlyFailedDownloadsPreservingOwnersAndPorts()
    {
        using var f=new Fixture();f.Start(3);await f.Drain(1);
        f.Infra.FailPrepare=true;f.Infra.PrepareError="Steam download failed";
        f.Start(0);f.Start(1);f.Start(2);await f.Drain(3);
        var admin=new User("admin","admin",true);var before=f.Pool.Snapshot(admin).Instances.ToDictionary(i=>i.Id,i=>(i.OwnerId,i.Ports));
        Assert.Throws<UnauthorizedAccessException>(()=>f.Pool.RetryFailedInstallations(Fixture.User(0)));
        f.Infra.FailPrepare=false;
        Assert.Equal(3,f.Pool.RetryFailedInstallations(admin).Count);
        Assert.Empty(f.Pool.RetryFailedInstallations(admin));
        Assert.Equal("running",f.Pool.Snapshot(Fixture.User(3)).Instances.Single().Phase);
        await f.Drain(3);
        foreach(var server in f.Pool.Snapshot(admin).Instances){Assert.Equal("running",server.Phase);Assert.Equal(before[server.Id],(server.OwnerId,server.Ports));}
        Assert.Equal(1,f.Infra.Rents);Assert.Equal(0,f.Infra.Deletes);Assert.Equal(4,f.Infra.Starts);
    }
}
