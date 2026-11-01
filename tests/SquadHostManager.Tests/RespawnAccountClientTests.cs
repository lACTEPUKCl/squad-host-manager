using System.Net;
using SquadHostManager.Core;
namespace SquadHostManager.Tests;

public class RespawnAccountClientTests
{
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(send(request));}
    [Fact] public async Task OwnAccountSetupOnlyReadsServersAndReturnsVerifiedKey()
    {
        var key="rsp_"+new string('a',64);int calls=0;
        using var http=new HttpClient(new Handler(r=>
        {calls++;Assert.Equal(HttpMethod.Get,r.Method);Assert.Equal("https://respawnhost.com/api/v1/servers?limit=1",r.RequestUri!.AbsoluteUri);Assert.Equal(key,r.Headers.GetValues("X-Api-Key").Single());return new(HttpStatusCode.OK){Content=new StringContent("{\"data\":[]}")};}));
        Assert.Equal(key,await new RespawnAccountClient(http).Inspect(" "+key+" "));Assert.Equal(1,calls);
    }
    [Fact] public async Task InvalidKeyIsRejectedWithoutANetworkRequest()
    {
        using var http=new HttpClient(new Handler(_=>throw new Exception("Unexpected request")));
        Assert.Equal("respawnKeyInvalid",(await Assert.ThrowsAsync<RespawnAccountException>(()=>new RespawnAccountClient(http).Inspect("invalid"))).MessageKey);
    }
    [Theory][InlineData(HttpStatusCode.Unauthorized)][InlineData(HttpStatusCode.Forbidden)]
    public async Task MissingServerScopeIsReportedWithoutExposingProviderResponse(HttpStatusCode status)
    {
        using var http=new HttpClient(new Handler(_=>new(status){Content=new StringContent("private response")}));
        Assert.Equal("respawnKeyInvalid",(await Assert.ThrowsAsync<RespawnAccountException>(()=>new RespawnAccountClient(http).Inspect("rsp_"+new string('a',64)))).MessageKey);
    }
}
