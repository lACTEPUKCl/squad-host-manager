using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;
public class RespawnRconSetupTests
{
    private const string Id="38c16e5a-0188-43c5-bd80-91f2c0a587f2";
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> handle):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(handle(r));}
    private static HttpResponseMessage Text(string text)=>new(HttpStatusCode.OK){Content=new StringContent(text,Encoding.UTF8,"application/json")};
    private static IConfiguration Config()=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token","rsp_"+new string('a',64)}}).Build();
    [Fact] public void VerifiedAllocatedConfigPortTakesPriorityOverAStaleDefaultLabel()
    {
        var info=System.Text.Json.JsonSerializer.SerializeToElement(new{connectionString="203.0.113.8:7740",variables=new[]{new{key="RCON_PORT",value="21114"}}});
        var details=System.Text.Json.JsonSerializer.SerializeToElement(new{panelServer=new{allocations=new[]{new{port=7740,isPrimary=true},new{port=7742,isPrimary=false}}}});
        Assert.Equal(7742,RespawnHost.ParseConnection(info,"Port=7742\nPassword=secret",details).RconPort);
    }
    [Fact] public async Task MissingAllocationsCannotSilentlySkipRconPreparation()
    {
        using var f=new Fixture();int writes=0;using var http=new HttpClient(new Handler(r=>{if(r.Method!=HttpMethod.Get)writes++;return Text("{}");}));using var api=new RespawnHost(f.Store,Config(),http);
        await Assert.ThrowsAsync<TimeoutException>(()=>api.ConfigureRcon(Id,"new-password",default,TimeSpan.Zero));Assert.Equal(0,writes);
    }
    [Fact] public async Task DelayedAllocationsAreWaitedForAndCanonicalConfigIsReadBack()
    {
        using var f=new Fixture();int details=0,reads=0,writes=0;var file="IP=127.0.0.1\nPort=21114\nPassword=\n";
        using var http=new HttpClient(new Handler(r=>
        {
            if(r.RequestUri!.AbsolutePath.EndsWith("/content")){reads++;return Text(file);}
            if(r.Method==HttpMethod.Post){writes++;file=r.Content!.ReadAsStringAsync().Result;return Text("{}");}
            details++;return Text(details==1?"{}":"""{"panelServer":{"allocations":[{"port":7740,"isPrimary":true},{"port":7741,"isPrimary":false},{"port":7742,"isPrimary":false}]}}""");
        }));using var api=new RespawnHost(f.Store,Config(),http);
        await api.ConfigureRcon(Id,"new-password",default,TimeSpan.FromSeconds(10));
        Assert.Equal(2,details);Assert.Equal(2,reads);Assert.Equal(1,writes);Assert.Contains("IP=0.0.0.0",file);Assert.Contains("Port=7742",file);Assert.Contains("Password=new-password",file);
    }
    [Fact] public async Task IgnoredFileWriteFailsVerification()
    {
        using var f=new Fixture();using var http=new HttpClient(new Handler(r=>r.RequestUri!.AbsolutePath.EndsWith("/content")?Text("Port=21114\nPassword=\n"):Text("""{"panelServer":{"allocations":[{"port":7740,"isPrimary":true},{"port":7742,"isPrimary":false}]}}""")));
        using var api=new RespawnHost(f.Store,Config(),http);await Assert.ThrowsAsync<IOException>(()=>api.ConfigureRcon(Id,"new-password",default));
    }
}
