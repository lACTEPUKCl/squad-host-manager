using SquadHostManager.Core;
using SquadHostManager.Service;
using Xunit;

namespace SquadHostManager.Tests;
public class PermanentSessionTests
{
    [Fact] public void PasswordAndKeySessionsRemainUntilLogoutOrRevocation()
    {
        using var fixture=new Fixture();var accounts=new Accounts(fixture.Store);accounts.Bootstrap("fixture-password-123");
        var owner=accounts.Login(new("admin","fixture-password-123"));var user=accounts.CreateUser(new(owner.UserId,"admin",true),"Member");var member=accounts.KeyLogin(new(user.Key));
        using var db=fixture.Store.Open();using var query=db.CreateCommand();query.CommandText="SELECT expires FROM sessions WHERE hash=$hash";query.Parameters.AddWithValue("$hash",Store.Hash(member.Token));
        Assert.True(DateTimeOffset.Parse((string)query.ExecuteScalar()!)>DateTimeOffset.UtcNow.AddYears(100));
        accounts.Logout(owner.Token);Assert.Null(accounts.Authenticate(owner.Token));
        accounts.RevokeUser(new(owner.UserId,"admin",true),member.UserId);Assert.Null(accounts.Authenticate(member.Token));
    }
    [Fact] public void MigrationPreservesValidSessionsWithoutRevivingExpiredTokens()
    {
        var folder=Path.Combine(Path.GetTempPath(),"shm-session-test-"+Guid.NewGuid().ToString("N"));string valid,expired;
        using(var store=new Store(folder))
        {
            var accounts=new Accounts(store);accounts.Bootstrap("fixture-password-123");valid=accounts.Login(new("admin","fixture-password-123")).Token;expired=accounts.Login(new("admin","fixture-password-123")).Token;
            using var db=store.Open();using var query=db.CreateCommand();query.CommandText="UPDATE sessions SET expires=$valid WHERE hash=$a; UPDATE sessions SET expires=$expired WHERE hash=$b; DELETE FROM documents WHERE id='permanent-sessions-v1'";
            query.Parameters.AddWithValue("$a",Store.Hash(valid));query.Parameters.AddWithValue("$b",Store.Hash(expired));query.Parameters.AddWithValue("$valid",DateTimeOffset.UtcNow.AddDays(1).ToString("O"));query.Parameters.AddWithValue("$expired",DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));query.ExecuteNonQuery();
        }
        using(var store=new Store(folder)){var accounts=new Accounts(store);Assert.NotNull(accounts.Authenticate(valid));Assert.Null(accounts.Authenticate(expired));}
        using(var store=new Store(folder))Assert.NotNull(new Accounts(store).Authenticate(valid));
        Directory.Delete(folder,true);
    }
}
