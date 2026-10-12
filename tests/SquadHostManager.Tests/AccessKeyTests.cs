using SquadHostManager.Core;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public class AccessKeyTests
{
    private static readonly User Owner = new("owner", "Owner", true);
    [Fact]
    public void PersonalKeyKeepsIdentityAndRotationInvalidatesOldSessions()
    {
        using var f = new Fixture(); var accounts = new Accounts(f.Store);
        var grant = accounts.CreateUser(Owner, "Tester"); var first = accounts.KeyLogin(new(grant.Key));
        Assert.False(first.IsAdmin); Assert.Equal(grant.User.Id, first.UserId);
        Assert.NotNull(accounts.Authenticate(first.Token));
        var next = accounts.RotateKey(Owner, first.UserId);
        Assert.Null(accounts.Authenticate(first.Token)); Assert.Throws<UnauthorizedAccessException>(() => accounts.KeyLogin(new(grant.Key)));
        Assert.Equal(first.UserId, accounts.KeyLogin(new(next.Key)).UserId);
        Assert.DoesNotContain(grant.Key, System.Text.Json.JsonSerializer.Serialize(accounts.Users(Owner)));
        using var db = f.Store.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT hash FROM access_keys";
        Assert.NotEqual(next.Key, cmd.ExecuteScalar());
    }
    [Fact]
    public void RevocationBlocksLoginAndCurrentSessionWithoutStoppingServer()
    {
        using var f = new Fixture(); var accounts = new Accounts(f.Store); var grant = accounts.CreateUser(Owner, "Tester");
        var session = accounts.KeyLogin(new(grant.Key)); accounts.RevokeUser(Owner, session.UserId);
        Assert.Null(accounts.Authenticate(session.Token)); Assert.Throws<UnauthorizedAccessException>(() => accounts.KeyLogin(new(grant.Key)));
        Assert.True(accounts.Users(Owner).Single().Disabled); Assert.Equal(0, f.Infra.Stops);
    }
    [Fact]
    public void ParticipantCannotIssueOrInspectKeys()
    {
        using var f = new Fixture(); var accounts = new Accounts(f.Store);
        Assert.Throws<UnauthorizedAccessException>(() => accounts.CreateUser(Fixture.User(1), "Tester"));
        Assert.Throws<UnauthorizedAccessException>(() => accounts.Users(Fixture.User(1)));
        Assert.Throws<UnauthorizedAccessException>(() => accounts.RotateKey(Fixture.User(1), "x"));
        Assert.Throws<UnauthorizedAccessException>(() => accounts.RevokeUser(Fixture.User(1), "x"));
    }
    [Fact]
    public async Task OriginalRenterSurvivesSecondParticipantAndRestart()
    {
        using var f = new Fixture(); f.Start(0); await f.Drain(1); f.Start(1); await f.Drain(1);
        Assert.Equal("User 0", f.Pool.Snapshot(Fixture.User(1)).Host.RentedByName);
        Assert.Equal("User 0", new PoolCoordinator(f.Store, f.Infra).Snapshot(Fixture.User(1)).Host.RentedByName);
    }
}
