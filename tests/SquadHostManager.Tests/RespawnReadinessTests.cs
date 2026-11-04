using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using SquadHostManager.Core;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;
public class RespawnReadinessTests
{
    private const string Uuid="38c16e5a-0188-43c5-bd80-91f2c0a587f2";
    private sealed class Handler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,r.Method);
            object value=r.RequestUri!.AbsolutePath.EndsWith("/servers")?new{data=new[]{new{panelUuid=Uuid,alias="Running",dockerState="running"}}}:r.RequestUri.AbsolutePath.EndsWith("/connection-info")?new{connectionString="203.0.113.8:7787",variables=new[]{new{key="RCON_PORT",value="21114"}}}:new{};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=r.RequestUri.AbsolutePath.EndsWith("/content")?new StringContent("Port=21114\nPassword=secret"):JsonContent.Create(value)});
        }
    }
    [Theory][InlineData("unavailable","running")][InlineData("ERROR: not ready","running")][InlineData("----- Active Players -----","gameReady")]
    public async Task RunningProviderDoesNotStayStartingWhenRconCannotConfirmReadiness(string reply,string expected)
    {
        using var f=new Fixture();using var http=new HttpClient(new Handler());
        using var api=new RespawnHost(f.Store,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token","rsp_"+new string('a',64)}}).Build(),http);
        f.Store.Save("respawn-instance-existing",new RespawnManagedServer(Uuid,"user1",PortSet.ForSlot(0),new(1070,8192,.13m,"eu")));
        using var infra=new RespawnInfrastructure(api,f.Store,(_,_,_)=>reply=="unavailable"?throw new IOException("RCON unavailable"):Task.FromResult(reply));
        Assert.Equal(expected,(await infra.Probe("respawn:test","respawnhost.com",new(){Id="existing"},default))!.State);
    }
}
