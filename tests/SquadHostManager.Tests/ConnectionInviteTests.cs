using SquadHostManager.Core;
namespace SquadHostManager.Tests;
public class ConnectionInviteTests
{
    [Fact] public void InvitationCarriesEndpointAndKeyTogether()
    {
        var invite=new ConnectionInvite("https://example.com/shm/","private-key");
        Assert.Equal(invite,ConnectionInvite.Decode(invite.Encode()));
        Assert.Null(ConnectionInvite.Decode("legacy-key"));
    }
    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("https://example.com/?secret=1")]
    [InlineData("file:///C:/data")]
    public void UnsafeEndpointsAreRejected(string endpoint)=>Assert.Throws<ArgumentException>(()=>ConnectionInvite.Decode(new ConnectionInvite(endpoint,"key").Encode()));
    [Fact] public void LocalPrivateAccountSupportsLoopback()=>Assert.NotNull(ConnectionInvite.Decode(new ConnectionInvite("http://127.0.0.1:18087/","key").Encode()));
    [Theory]
    [InlineData("SHM1.invalid!")]
    [InlineData("SHM1.bnVsbA")]
    public void MalformedInvitationIsRejected(string value)=>Assert.Throws<ArgumentException>(()=>ConnectionInvite.Decode(value));
}
