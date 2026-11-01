using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Configuration;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;
public class RespawnHostTests
{
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(send(request));}
    private static HttpResponseMessage Json(string value,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(value,Encoding.UTF8,"application/json")};
    [Fact] public async Task OnlyEightGbPlanIsOffered()
    {
        using var f=new Fixture();
        using var http=new HttpClient(new Handler(_=>Json("""[{"id":1069,"memory":4096,"priceHourly":0.07},{"id":1070,"memory":8192,"priceHourly":0.13324999},{"id":1071,"memory":12288,"priceHourly":0.20417021},{"id":1072,"memory":16384,"priceHourly":0.27817391}]""")));
        using var api=new RespawnHost(f.Store,new ConfigurationBuilder().Build(),http);
        Assert.Equal(1070,Assert.Single(await api.Plans(default)).Id);
    }
    [Fact] public async Task OtherRegionsAreRejectedBeforeSendingAPaidOrder()
    {
        using var f=new Fixture();int posts=0;
        using var http=new HttpClient(new Handler(r=>{if(r.Method!=HttpMethod.Get)posts++;return Json("""[{"id":1070,"memory":8192,"priceHourly":0.13324999}]""");}));
        using var api=new RespawnHost(f.Store,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token","rsp_"+new string('a',64)}}).Build(),http);
        await Assert.ThrowsAsync<ArgumentException>(()=>api.Rent(new(Guid.NewGuid(),1070,0.13324999m,"us"),default));
        Assert.Equal(0,posts);Assert.False(api.Pending);
    }
    [Fact] public async Task TokenIsSentOnlyToAuthenticatedEndpointsAndStoppedServersAllowSwitch()
    {
        using var f=new Fixture();var key="rsp_"+new string('a',64);
        using var http=new HttpClient(new Handler(r=>{Assert.Equal("GET",r.Method.Method);Assert.Equal(key,r.Headers.GetValues("X-Api-Key").Single());return Json("{\"data\":[{\"panelUuid\":\"38c16e5a-0188-43c5-bd80-91f2c0a587f2\",\"alias\":\"Training\",\"dockerState\":\"offline\"}],\"summary\":{\"total\":1}}");}));
        var client=new RespawnHost(f.Store,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token",key}}).Build(),http);
        Assert.False(await client.HasServers(default));Assert.Equal("Training",(await client.Servers(default)).Single().Name);
    }
    [Theory][InlineData("running")][InlineData("starting")][InlineData("unknown")]
    public async Task ActiveOrUnknownServerStateBlocksSwitch(string state)
    {
        using var f=new Fixture();using var http=new HttpClient(new Handler(_=>Json("{\"data\":[{\"panelUuid\":\"38c16e5a-0188-43c5-bd80-91f2c0a587f2\",\"dockerState\":\""+state+"\"}],\"summary\":{\"total\":1}}")));
        var client=new RespawnHost(f.Store,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token","rsp_"+new string('a',64)}}).Build(),http);
        Assert.True(await client.HasServers(default));
    }
    [Fact] public async Task VerifiedTokenRotationSurvivesServiceRestartWithoutExposingTheKey()
    {
        using var f=new Fixture();var original="rsp_"+new string('a',64);var replacement="rsp_"+new string('b',64);var headers=new List<string>();
        using var http=new HttpClient(new Handler(r=>{headers.Add(r.Headers.GetValues("X-Api-Key").Single());return Json("{\"data\":[],\"summary\":{\"total\":0}}");}));
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token",original}}).Build();
        using(var first=new RespawnHost(f.Store,config,http))await first.SaveToken(replacement,default);
        using var restored=new RespawnHost(f.Store,config,http);Assert.Empty(await restored.Servers(default));Assert.Equal(replacement,headers.Last());
        Assert.Equal(replacement,File.ReadAllText(Path.Combine(f.Store.DirectoryPath,"respawn-token.txt")));
        Assert.DoesNotContain(Directory.EnumerateFiles(f.Store.DirectoryPath),p=>Path.GetFileName(p).StartsWith("respawn-token.txt.",StringComparison.Ordinal));
    }
    [Fact] public async Task ReconnectingWithTheSameKeyDoesNotRequireStoppingServers()
    {
        using var f=new Fixture();var key="rsp_"+new string('a',64);
        using var http=new HttpClient(new Handler(_=>Json("{\"data\":[{\"dockerState\":\"running\"}]}")));
        using var api=new RespawnHost(f.Store,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token",key}}).Build(),http);
        await api.SaveToken(key,default);Assert.Equal(key,File.ReadAllText(Path.Combine(f.Store.DirectoryPath,"respawn-token.txt")));
    }
    [Fact] public async Task ExplicitlyRejectedOrderDoesNotBlockANewAttemptAfterTopUp()
    {
        using var f=new Fixture();int posts=0;
        using var http=new HttpClient(new Handler(r=>r.Method==HttpMethod.Get?Json("""[{"id":1070,"memory":8192,"priceHourly":0.13324999}]"""):Reject()));
        HttpResponseMessage Reject(){posts++;return Json("{}",HttpStatusCode.PaymentRequired);}
        using var api=new RespawnHost(f.Store,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token","rsp_"+new string('a',64)}}).Build(),http);
        var request=new SquadHostManager.Core.RespawnRentRequest(Guid.NewGuid(),1070,0.13324999m,"eu");
        await Assert.ThrowsAsync<RespawnApiException>(()=>api.Rent(request,default));Assert.False(api.Pending);
        await Assert.ThrowsAsync<RespawnApiException>(()=>api.Rent(request,default));Assert.Equal(2,posts);
        await Assert.ThrowsAsync<RespawnApiException>(()=>api.Rent(request with{RequestId=Guid.NewGuid()},default));Assert.Equal(3,posts);
    }
    [Fact] public async Task EmptyUnconfiguredAccountDoesNotTrapProviderSelection()
    {
        using var f=new Fixture();using var client=new RespawnHost(f.Store,new ConfigurationBuilder().Build());
        Assert.False(await client.HasServers(default));
        f.Store.Save("respawn-account-connected",true);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>client.HasServers(default));
    }
    [Fact] public async Task StaleOfflineReplyDoesNotAllowSwitchWhileStartIsPending()
    {
        using var f=new Fixture();var id="38c16e5a-0188-43c5-bd80-91f2c0a587f2";
        f.Store.Save("respawn-power-pending",new Dictionary<string,string>{{id,"start"}});
        using var http=new HttpClient(new Handler(_=>Json("{\"data\":[{\"panelUuid\":\""+id+"\",\"dockerState\":\"offline\"}],\"summary\":{\"total\":1}}")));
        using var client=new RespawnHost(f.Store,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token","rsp_"+new string('a',64)}}).Build(),http);
        Assert.True(await client.HasServers(default));
    }
    [Fact] public async Task RentalAlwaysRequestsOneHourlySquadAndDoesNotReplayACompletedRequest()
    {
        using var f=new Fixture();int posts=0;
        using var http=new HttpClient(new Handler(r=>
        {
            if(r.Method==HttpMethod.Get){Assert.False(r.Headers.Contains("X-Api-Key"));return Json("[{\"id\":1070,\"memory\":8192,\"priceHourly\":0.13324999}]");}
            posts++;using var json=System.Text.Json.JsonDocument.Parse(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var body=json.RootElement;Assert.Equal("hourly",body.GetProperty("billing_mode").GetString());Assert.Equal("squad",body.GetProperty("game_short").GetString());Assert.Equal(1,body.GetProperty("instance_count").GetInt32());
            Assert.True(r.Headers.Contains("X-Api-Key"));return Json("{\"panelUuid\":\"38c16e5a-0188-43c5-bd80-91f2c0a587f2\"}");
        }));
        using var client=new RespawnHost(f.Store,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token","rsp_"+new string('a',64)}}).Build(),http);
        var request=new SquadHostManager.Core.RespawnRentRequest(Guid.NewGuid(),1070,0.13324999m,"eu");
        await client.Rent(request,default);await Assert.ThrowsAsync<InvalidOperationException>(()=>client.Rent(request,default));Assert.Equal(1,posts);
    }
    [Fact] public async Task UncertainRentIsNotAutomaticallyRetried()
    {
        using var f=new Fixture();int posts=0;using var http=new HttpClient(new Handler(r=>r.Method==HttpMethod.Get?Json("[{\"id\":1070,\"memory\":8192,\"priceHourly\":0.13324999}]"):Fail()));
        HttpResponseMessage Fail(){posts++;throw new HttpRequestException("lost response");}
        var client=new RespawnHost(f.Store,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token","rsp_"+new string('a',64)}}).Build(),http);
        var request=new SquadHostManager.Core.RespawnRentRequest(Guid.NewGuid(),1070,0.13324999m,"eu");
        await Assert.ThrowsAsync<HttpRequestException>(()=>client.Rent(request,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>client.Rent(request,default));Assert.Equal(1,posts);
        Assert.True(await client.HasServers(default));
    }
}
