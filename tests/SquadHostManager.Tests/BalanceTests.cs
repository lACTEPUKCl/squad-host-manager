using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public sealed class BalanceTests
{
    [Fact] public async Task AccountBalanceIsCachedAcrossRequests()
    {
        using var fixture=new Fixture();var balance=new AccountBalance(fixture.Infra);
        var first=await balance.Read(default);var second=await balance.Read(default);
        Assert.Equal(28.31m,first.Available);Assert.Equal(first,second);Assert.Equal(1,fixture.Infra.BalanceReads);
    }
    [Fact] public async Task UnavailableBalanceIsUnknownInsteadOfZero()
    {
        using var fixture=new Fixture();fixture.Infra.BalanceFails=true;
        var balance=await new AccountBalance(fixture.Infra).Read(default);
        Assert.Null(balance.Available);Assert.True(balance.Stale);
    }
}
