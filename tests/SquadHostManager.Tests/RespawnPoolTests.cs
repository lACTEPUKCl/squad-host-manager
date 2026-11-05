using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SquadHostManager.Core;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;

public partial class RespawnPoolTests
{
    private sealed class Backend:HttpMessageHandler
    {
        public TaskCompletionSource? Installation;
        public Task<IRespawnInstallMonitor> Watch(string id,CancellationToken ct)
        {Installation=new(TaskCreationOptions.RunContinuationsAsynchronously);return Task.FromResult<IRespawnInstallMonitor>(new Monitor(Installation));}
        private sealed class Monitor(TaskCompletionSource completion):IRespawnInstallMonitor
        {public Task Wait(CancellationToken ct)=>completion.Task.WaitAsync(ct);public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
        public readonly Dictionary<string,string> Files=new(){{"/SquadGame/ServerConfig/Server.cfg",SquadConfig.Initial},{"/SquadGame/ServerConfig/Rcon.cfg","Port=21114\nPassword=secret"},{"/SquadGame/Saved/Logs/SquadGame.log","server log"},{"/SquadGame/Saved/OSI_Scoreboards/result.json","{\"winner\":1}"}};
        public readonly List<string> Calls=[];
        public bool RejectRental, LoseRentalResponse, MissingRconAllocations;
        public string Read(string path)=>servers[Uuid!].Files[path];
        public string Read(string uuid,string path)=>servers[uuid].Files[path];
        private sealed class Server {public string State="offline";public int Installing;public Dictionary<string,string> Files=[];}
        private readonly Dictionary<string,Server> servers=[];
        public string? Uuid;public string State=>servers.GetValueOrDefault(Uuid??"")?.State??"offline";public int Rentals;
        private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=JsonContent.Create(value)};
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            var path=r.RequestUri!.AbsolutePath;lock(Calls)Calls.Add(r.Method+" "+path);
            if(path.EndsWith("/packages"))return Json(new[]{new{id=1070,memory=8192,priceHourly=0.13324999m}});
            if(path.EndsWith("/servers/rent")){if(RejectRental)return new(HttpStatusCode.BadRequest){Content=JsonContent.Create(new{message="Server limit reached"})};Rentals++;Uuid=Guid.NewGuid().ToString();servers[Uuid]=new(){Files=new(Files)};if(LoseRentalResponse)throw new HttpRequestException("Lost rent reply");return Json(new{panelUuid=Uuid});}
            if(path.EndsWith("/servers"))
            {
                var rows=servers.Select(s=>new{panelUuid=s.Key,alias="Training",dockerState=s.Value.State=="installing"?"offline":s.Value.State}).ToArray();

                return Json(new{data=rows,summary=new{total=rows.Length}});
            }
            var uuid=path.Split('/').Length>4?path.Split('/')[4]:"";
            var current=servers.GetValueOrDefault(uuid);
            var files=current?.Files??Files;
            if(path.EndsWith("/reinstall")){Assert.True((await r.Content!.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("keep_files").GetBoolean());Assert.NotNull(Installation);current!.State="offline";current.Files.Remove("/SquadGame/Saved/Logs/SquadGame.log");Installation.SetResult();return Json(new{});}
            if(path.EndsWith("/powerstate")){var body=await r.Content!.ReadFromJsonAsync<JsonElement>(ct);current!.State=body.GetProperty("power_state").GetString()=="stop"?"offline":"running";return Json(new{});}
            if(path.EndsWith("/startup-information/en"))return Json(new{data=new[]{new{attributes=new{env_variable="WORKSHOP_IDS",server_value="",is_editable=true}}}});
            if(path.EndsWith("/connection-info"))return Json(new{connectionString="203.0.113.8:7787",variables=new[]{new{key="RCON_PORT",value="21114"}}});
            if(path.EndsWith(uuid) && current!=null)return MissingRconAllocations?Json(new{}):Json(new{panelServer=new{allocations=new[]{new{port=7787,isPrimary=true},new{port=21114,isPrimary=false}}}});
            if(path.EndsWith("/resource-utilization")){var state=current!.State;if(current.Installing>0 && --current.Installing==0)current.State="offline";return Json(new{current_state=state,resources=new{cpu_absolute=92,memory_bytes=4L*1024*1024*1024,disk_bytes=1000000}});}
            var query=System.Web.HttpUtility.ParseQueryString(r.RequestUri.Query);
            if(path.EndsWith("/files/content")){var file=query["file"]!;return files.TryGetValue(file,out var text)?new(HttpStatusCode.OK){Content=new StringContent(text)}:new(HttpStatusCode.NotFound);}
            if(path.EndsWith("/files/write")){files[query["file"]!]=await r.Content!.ReadAsStringAsync(ct);return Json(new{});}
            if(path.EndsWith("/files")){var directory=query["directory"]!+"/";var names=files.Keys.Where(k=>k.StartsWith(directory)).Select(k=>k[directory.Length..].Split('/')[0]).Distinct();return Json(new{data=names.Select(name=>new{attributes=new{name,size=files.TryGetValue(directory+name,out var text)?Encoding.UTF8.GetByteCount(text):0,is_file=files.ContainsKey(directory+name),is_symlink=false}}).ToArray()});}
            return Json(new{});
        }
    }
    [Fact] public async Task ThreeUsersCanQueueSeparateServersConcurrentlyWithoutDuplicateRentals()
    {
        using var f=new Fixture();using var backend=new Backend();using var http=new HttpClient(backend);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token","rsp_"+new string('a',64)}}).Build();
        using var api=new RespawnHost(f.Store,config,http,backend.Watch);
        using var infra=new RespawnInfrastructure(api,f.Store,(_,_,_)=>Task.FromResult("----- Active Players -----"));
        f.Store.Save("pool",new PoolState{Provider="respawn",HostId="respawn:test",Ip="respawnhost.com",HostPhase="ready"});
        var pool=new PoolCoordinator(f.Store,infra);
        var requests=Enumerable.Range(1,3).Select(i=>new RespawnRentRequest(Guid.NewGuid(),1070,0.13324999m,"eu",new("Server "+i,"password-"+i,"",24))).ToArray();
        var queued=await Task.WhenAll(Enumerable.Range(1,3).Select(i=>Task.Run(()=>pool.StartRespawn(Fixture.User(i),requests[i-1],api,default))));
        Assert.All(queued,o=>Assert.Equal("queued",o.Phase));Assert.Equal(0,backend.Rentals);
        var positions=Enumerable.Range(1,3).Select(i=>Assert.Single(pool.Snapshot(Fixture.User(i)).Instances).QueuePosition).Order().ToArray();
        Assert.Equal(new int?[]{1,2,3},positions);
        await Task.WhenAll(Enumerable.Range(0,3).Select(_=>pool.ProcessNext(default)));
        Assert.Equal(3,backend.Rentals);
        var mappings=new List<string>();
        for(var i=1;i<=3;i++)
        {
            var instance=Assert.Single(pool.Snapshot(Fixture.User(i)).Instances);Assert.Equal("running",instance.Phase);
            var uuid=f.Store.Load<RespawnManagedServer>("respawn-instance-"+instance.Id)!.Uuid;mappings.Add(uuid);
            var text=backend.Read(uuid,"/SquadGame/ServerConfig/Server.cfg");Assert.Contains("Server "+i,text);Assert.Contains("password-"+i,text);
            Assert.Equal(queued[i-1].Id,(await pool.StartRespawn(Fixture.User(i),requests[i-1],api,default)).Id);
        }
        Assert.Equal(3,mappings.Distinct().Count());Assert.Equal(3,backend.Rentals);
        await pool.ProcessNext(default);Assert.Equal(3,backend.Rentals);
    }
    [Fact] public async Task FirstModInstallWithoutLogsCreatesAValidEmptyRecoveryArchive()
    {
        using var f=new Fixture();using var backend=new Backend();using var http=new HttpClient(backend);
        foreach(var name in backend.Files.Keys.Where(k=>k.StartsWith("/SquadGame/Saved/")).ToList())backend.Files.Remove(name);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token","rsp_"+new string('a',64)}}).Build();
        using var api=new RespawnHost(f.Store,config,http,backend.Watch);using var infra=new RespawnInfrastructure(api,f.Store);
        f.Store.Save("pool",new PoolState{Provider="respawn",HostId="respawn:test",Ip="respawnhost.com",HostPhase="ready"});
        f.Store.Save("respawn-plan-user1",new RespawnAllocation(1070,8192,0.13324999m,"eu"));
        var pool=new PoolCoordinator(f.Store,infra);pool.Start(Fixture.User(1),new(Guid.NewGuid(),"First server","","123",24));await pool.ProcessNext(default);
        Assert.Equal("running",Assert.Single(pool.Snapshot(Fixture.User(1)).Instances).Phase);Assert.Equal(1,backend.Rentals);
        using var archive=await infra.ArchiveStream("respawn:test","respawnhost.com",Assert.Single(pool.Snapshot(Fixture.User(1)).Instances).Id,default);
        Assert.True(archive.Length>0);BackupFormat.ValidateFiles(archive);
    }
    [Fact] public async Task ExistingConfigAdminsPlayersFilesAndSavedArchivesUseRespawnAdapter()
    {
        using var f=new Fixture();using var backend=new Backend();using var http=new HttpClient(backend);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token","rsp_"+new string('a',64)}}).Build();
        using var api=new RespawnHost(f.Store,config,http,backend.Watch);var commands=new List<string>();
        using var infra=new RespawnInfrastructure(api,f.Store,(_,command,_)=>{commands.Add(command);return Task.FromResult(command=="ListPlayers"?"----- Active Players -----":"OK");});
        f.Store.Save("pool",new PoolState{Provider="respawn",HostId="respawn:test",Ip="respawnhost.com",HostPhase="ready"});
        f.Store.Save("respawn-plan-user1",new RespawnAllocation(1070,8192,0.13324999m,"eu"));
        var pool=new PoolCoordinator(f.Store,infra);pool.DefaultConfig(Fixture.User(1),new("Admins.cfg","Group=Operators:kick\nAdmin=76561198000000001:Operators"));
        pool.Start(Fixture.User(1),new(Guid.NewGuid(),"Tournament","game-password","123 456",100));await pool.ProcessNext(default);
        var instance=Assert.Single(pool.Snapshot(Fixture.User(1)).Instances);Assert.Equal("running",instance.Phase);Assert.Equal(1,backend.Rentals);
        Assert.Contains("Tournament",backend.Read("/SquadGame/ServerConfig/Server.cfg"));Assert.Contains("game-password",backend.Read("/SquadGame/ServerConfig/Server.cfg"));Assert.Contains("Operators",backend.Read("/SquadGame/ServerConfig/Admins.cfg"));
        await pool.Reconcile(default);Assert.Equal("gameReady",pool.Snapshot(Fixture.User(1)).Instances.Single().GameState);
        await pool.SaveCommonConfig(Fixture.User(1),new("Admins.cfg","Group=Moderators:kick,ban"),default);
        Assert.Contains("AdminReloadServerConfig",commands);Assert.Contains("Moderators",backend.Read("/SquadGame/ServerConfig/Admins.cfg"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>pool.Access(Fixture.User(2),instance.Id,"AdminKick 76561198000000001 reason",default));
        Assert.Equal("OK",await pool.Access(Fixture.User(1),instance.Id,"AdminKick 76561198000000001 reason",default));
        Assert.Equal(2,(await pool.Files(Fixture.User(1),instance.Id,default)).Count);
        pool.Restart(Fixture.User(1),instance.Id,new(Guid.NewGuid()));await pool.ProcessNext(default);
        Assert.Contains(backend.Calls,c=>c.EndsWith("/reinstall"));Assert.Equal(1,backend.Rentals);Assert.Equal("running",pool.Snapshot(Fixture.User(1)).Instances.Single().Phase);Assert.Equal("running",backend.State);
        Assert.True(pool.Features(Fixture.User(1),instance.Id).HistoryIncomplete);Assert.True(pool.Features(Fixture.User(1),instance.Id).Costs.Single().Spent<0.01m);
        Assert.Equal("server log",backend.Read("/SquadGame/Saved/Logs/SquadGame.log"));
        pool.Stop(Fixture.User(1),new(Guid.NewGuid(),true,instance.Id));await pool.ProcessNext(default);
        Assert.Equal("offline",backend.State);Assert.Null(pool.Snapshot(Fixture.User(1)).Host.Id);
        await using var saved=await pool.ArchiveStream(Fixture.User(1),instance.Id,default);Assert.True(saved.Length>0);
        Assert.Single(pool.Tools(Fixture.User(1),null).Matches);Assert.Equal(1,backend.Rentals);
        Assert.DoesNotContain(backend.Calls,c=>c.StartsWith("DELETE"));
    }
}
