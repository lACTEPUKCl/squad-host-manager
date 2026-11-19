using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Configuration;
using SquadHostManager.Core;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;
public class RespawnManagementTests
{
    private const string Id="38c16e5a-0188-43c5-bd80-91f2c0a587f2";
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(send(request));}
    private static HttpResponseMessage Json(string text)=>new(HttpStatusCode.OK){Content=new StringContent(text,Encoding.UTF8,"application/json")};
    private static IConfiguration Config()=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token","rsp_"+new string('a',64)}}).Build();
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListedLogContent404UsesSignedDownloadWithoutForwardingApiKey(bool streaming)
    {
        using var f=new Fixture();var calls=new List<string>();
        using var http=new HttpClient(new Handler(r=>
        {
            calls.Add(r.RequestUri!.AbsolutePath);Assert.Equal(HttpMethod.Get,r.Method);
            if(r.RequestUri.Host=="node3.eu.respawnhost.com")
            {Assert.False(r.Headers.Contains("X-Api-Key"));return new(HttpStatusCode.OK){Content=new StringContent("complete log")};}
            Assert.True(r.Headers.Contains("X-Api-Key"));
            Assert.Contains("file=%2FSquadGame%2FSaved%2FLogs%2FSquadGame.log",r.RequestUri.Query);
            return r.RequestUri.AbsolutePath.EndsWith("/content")?new(HttpStatusCode.NotFound):Json("{\"url\":\"https://node3.eu.respawnhost.com:8080/download?token=private\"}");
        }));
        using var api=new RespawnHost(f.Store,Config(),http);
        using var output=new MemoryStream();
        if(streaming)await api.CopyFile(Id,"/SquadGame/Saved/Logs/SquadGame.log",output,default);
        else await output.WriteAsync(await api.ReadFile(Id,"/SquadGame/Saved/Logs/SquadGame.log",default));
        Assert.Equal("complete log",Encoding.UTF8.GetString(output.ToArray()));Assert.Equal(3,calls.Count);
    }
    [Theory]
    [InlineData("http://node3.eu.respawnhost.com/download")]
    [InlineData("https://respawnhost.com.evil.test/download")]
    [InlineData("https://user@node3.eu.respawnhost.com/download")]
    public async Task SignedDownloadRejectsUntrustedAddresses(string url)
    {
        using var f=new Fixture();int calls=0;
        using var http=new HttpClient(new Handler(r=>{calls++;return r.RequestUri!.AbsolutePath.EndsWith("/content")?new(HttpStatusCode.NotFound):Json(System.Text.Json.JsonSerializer.Serialize(new{url}));}));
        using var api=new RespawnHost(f.Store,Config(),http);
        await Assert.ThrowsAsync<IOException>(()=>api.ReadFile(Id,"/SquadGame/Saved/Logs/SquadGame.log",default));Assert.Equal(2,calls);
    }
    [Fact] public async Task NewServerWaitsForStartupVariablesWithoutOrderingAgain()
    {
        using var f=new Fixture();int reads=0;
        using var http=new HttpClient(new Handler(r=>{Assert.Equal(HttpMethod.Get,r.Method);return ++reads==1?Json("{\"variables\":[]}"):Json("""{"variables":[{"envVariable":"WORKSHOP_ITEMS","serverValue":"","isEditable":true}]}""");}));
        using var api=new RespawnHost(f.Store,Config(),http);
        await api.WaitStartup(Id,default,TimeSpan.FromSeconds(10));Assert.Equal(2,reads);
    }
    [Fact] public async Task EmptyStartupTimeoutDoesNotApplyOrOrderAnything()
    {
        using var f=new Fixture();using var http=new HttpClient(new Handler(r=>{Assert.Equal(HttpMethod.Get,r.Method);return Json("{\"variables\":[]}");}));
        using var api=new RespawnHost(f.Store,Config(),http);
        await Assert.ThrowsAsync<TimeoutException>(()=>api.WaitStartup(Id,default,TimeSpan.Zero));Assert.False(api.Pending);
    }
    [Fact] public async Task ConfigUsesRawFileBodyAndEncodedPath()
    {
        using var f=new Fixture();var calls=new List<string>();using var http=new HttpClient(new Handler(r=>{calls.Add(r.Method+" "+r.RequestUri!.PathAndQuery);if(r.Method==HttpMethod.Post){Assert.Equal("Group=Admin:kick",r.Content!.ReadAsStringAsync().Result);return Json("{}");}return new(HttpStatusCode.OK){Content=new StringContent("Group=Admin:kick")};}));
        using var client=new RespawnHost(f.Store,Config(),http);
        await client.WriteFile(Id,"/SquadGame/ServerConfig/Admins.cfg",Encoding.UTF8.GetBytes("Group=Admin:kick"),default);
        Assert.Equal("Group=Admin:kick",Encoding.UTF8.GetString(await client.ReadFile(Id,"/SquadGame/ServerConfig/Admins.cfg",default)));
        Assert.All(calls,c=>Assert.Contains("file=%2FSquadGame%2FServerConfig%2FAdmins.cfg",c));
        await Assert.ThrowsAsync<ArgumentException>(()=>client.ReadFile(Id,"/SquadGame/../../secret",default));
    }
    [Fact] public async Task RemovedWorkshopFoldersArePrunedWithoutTouchingBuiltInOrSelectedMods()
    {
        using var fixture = new Fixture();
        bool deleted = false;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var body = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement;
                Assert.Equal("/SquadGame/Plugins/Mods", body.GetProperty("root").GetString());
                Assert.Equal(new[]{"22","33"}, body.GetProperty("files").EnumerateArray().Select(x => x.GetString()).ToArray());
                deleted = true;
                return Json("{}");
            }
            var directory = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["directory"];
            string[] names = directory == "/SquadGame/Plugins" ? ["Mods"] : deleted ? ["11","CanadianArmedForces"] : ["11","22","33","CanadianArmedForces"];
            return Json(System.Text.Json.JsonSerializer.Serialize(new {data = names.Select(name => new {name, file=false})}));
        }));
        using var api = new RespawnHost(fixture.Store, Config(), http);
        await api.RemoveUnselectedWorkshopMods(Id,"11",default);
        Assert.True(deleted);
    }
    [Fact] public async Task WorkshopSettingsUseActualProviderVariableAndRejectUnsupportedMods()
    {
        using var f=new Fixture();int writes=0;using var http=new HttpClient(new Handler(r=>{if(r.Method==HttpMethod.Get)return Json(System.Text.Json.JsonSerializer.Serialize(new {data=new[]{new {attributes=new {env_variable="WORKSHOP_IDS",server_value=writes==0?"":"123 456",is_editable=true}}}}));writes++;Assert.Contains("123 456",r.Content!.ReadAsStringAsync().Result);return Json("{}");}));
        using var client=new RespawnHost(f.Store,Config(),http);await client.ApplyWorkshop(Id,"123 456",default);Assert.Equal(1,writes);
        using var emptyHttp=new HttpClient(new Handler(_=>Json("{\"data\":[]}")));using var missing=new RespawnHost(f.Store,Config(),emptyHttp);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>missing.ApplyWorkshop(Id,"123",default));
    }
    [Fact] public async Task IgnoredWorkshopRemovalBlocksStartup()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(new Handler(r => r.Method == HttpMethod.Put ? Json("{}") :
            Json("""{"variables":[{"envVariable":"WORKSHOP_ITEMS","serverValue":"11,22,33","isEditable":true}]}""")));
        using var api = new RespawnHost(fixture.Store,Config(),http);
        await Assert.ThrowsAsync<IOException>(() => api.ApplyWorkshop(Id,"11",default));
    }
    [Fact] public void ConnectionsNeverGuessOrUseConsoleAcknowledgementAsRconReply()
    {
        Assert.Throws<InvalidOperationException>(()=>RespawnHost.ParseConnection(System.Text.Json.JsonSerializer.SerializeToElement(new{connectionString="203.0.113.4:7787"}),"Port=21114\nPassword=secret"));
        var info=RespawnHost.ParseConnection(System.Text.Json.JsonSerializer.SerializeToElement(new{connectionString="203.0.113.4:7787",variables=new[]{new{key="RCON_PORT",value="21114"}}}),"Port=21114\nPassword=secret");
        Assert.Equal("203.0.113.4",info.Host);Assert.Equal(21114,info.RconPort);
    }
    [Fact] public async Task LiveNodeFileAndStartupSchemasAreSupported()
    {
        using var f=new Fixture();var writes=new List<string>();
        using var http=new HttpClient(new Handler(r=>
        {
            if(r.Method==HttpMethod.Put){writes.Add(r.Content!.ReadAsStringAsync().Result);return Json("{}");}
            if(r.RequestUri!.AbsolutePath.EndsWith("/files"))return Json("""{"entries":{"total":2,"data":[{"name":"Logs","size":0,"directory":true,"file":false,"symlink":false},{"name":"sessionId.txt","size":36,"file":true,"symlink":false}]}}""");
            return Json("""{"variables":[{"envVariable":"WORKSHOP_ITEMS","serverValue":"","isEditable":true},{"envVariable":"servername","serverValue":"","isEditable":true}]}""".Replace("\"WORKSHOP_ITEMS\",\"serverValue\":\"\"",writes.Count==0?"\"WORKSHOP_ITEMS\",\"serverValue\":\"\"":"\"WORKSHOP_ITEMS\",\"serverValue\":\"123,456\""));
        }));
        using var client=new RespawnHost(f.Store,Config(),http);
        var entries=await client.ListFiles(Id,"/SquadGame/Saved",default);
        Assert.False(entries[0].File);Assert.Equal(36,entries[1].Bytes);
        await client.ApplyWorkshop(Id,"123 456",default);
        await client.ApplyIdentity(Id,new Instance{Name="Training"},default);
        Assert.Contains(writes,w=>w.Contains("123,456"));Assert.Contains(writes,w=>w.Contains("servername")&&w.Contains("Training"));
    }
}
