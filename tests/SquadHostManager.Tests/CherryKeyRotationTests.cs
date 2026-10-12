using System.Net;
using Microsoft.Extensions.Configuration;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;
public class CherryKeyRotationTests
{
    private sealed class Requests : HttpMessageHandler
    {
        public List<string?> Keys { get; }=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            Assert.Equal(HttpMethod.Get,request.Method);
            Keys.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
    [Fact] public async Task ChangingKeyAppliesToNextRequestWithoutRecreatingInfrastructure()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Cherry:Token"]="old-key"}).Build();
        var folder=Path.Combine(Path.GetTempPath(),"shm-key-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            var requests=new Requests();using var client=new HttpClient(requests){BaseAddress=new Uri("https://api.cherryservers.com/v1/")};
            using var store=new Store(folder);
            using var infrastructure=new CherryInfrastructure(config,store,client);
            Assert.Null(await infrastructure.GetHost("123",default));
            config["Cherry:Token"]="new-key";
            Assert.Null(await infrastructure.GetHost("123",default));
            Assert.Equal(new[]{"old-key","new-key"},requests.Keys);
        }
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
}
