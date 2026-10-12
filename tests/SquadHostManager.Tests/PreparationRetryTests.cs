using SquadHostManager.Core;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;
public class PreparationRetryTests
{
    [Fact] public async Task RetryUsesExistingHostAndNeverRentsWhenHostChanges()
    {
        using var f=new Fixture();f.Start(0);await f.Drain(1);var admin=Fixture.User(0) with{IsAdmin=true};
        var request=new RestartRequest(Guid.NewGuid());var first=f.Pool.RetryHostPreparation(admin,request);
        Assert.Equal(first.Id,f.Pool.RetryHostPreparation(admin,request).Id);await f.Drain(1);Assert.Equal(1,f.Infra.Rents);
        f.Pool.RetryHostPreparation(admin,new(Guid.NewGuid()));f.Infra.Host=null;await f.Drain(1);Assert.Equal(1,f.Infra.Rents);
    }
    [Fact] public void RetryRequiresAdministratorAndExistingHost()
    {
        using var f=new Fixture();Assert.Throws<UnauthorizedAccessException>(()=>f.Pool.RetryHostPreparation(Fixture.User(1),new(Guid.NewGuid())));
        Assert.Throws<InvalidOperationException>(()=>f.Pool.RetryHostPreparation(Fixture.User(0) with{IsAdmin=true},new(Guid.NewGuid())));
    }
}
