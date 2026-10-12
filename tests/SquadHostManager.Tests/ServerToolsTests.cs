using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using SquadHostManager.Core;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;
public sealed class ServerToolsTests
{
    private static string Id(Fixture f)=>f.Pool.Snapshot(Fixture.User(1)).Instances.First().Id;
    private static byte[] Tar(string name="Logs/match.log",TarEntryType type=TarEntryType.RegularFile)
    {
        using var result=new MemoryStream();using(var gzip=new GZipStream(result,CompressionLevel.Fastest,true))using(var tar=new TarWriter(gzip,true))
        {var entry=new PaxTarEntry(type,name);if(type==TarEntryType.SymbolicLink)entry.LinkName="/etc/shadow";else entry.DataStream=new MemoryStream(Encoding.UTF8.GetBytes("test logs"));tar.WriteEntry(entry);}return result.ToArray();
    }
    [Fact] public async Task BackupRoundTripPreservesSettingsConfigsFilesAndNewPorts()
    {
        using var f=new Fixture();f.Infra.ArchiveData=Tar();f.Pool.Start(Fixture.User(1),new(Guid.NewGuid(),"Source","secret","3816653940",100,StartLayer:"FlightSchool"));await f.Drain(1);var id=Id(f);f.Infra.Configurations[(id,"Admins.cfg")]="Group=Owner:chat,kick";
        var backup=await f.Pool.CreateBackup(Fixture.User(1),id,new("Before changes"),default);
        await using(var input=f.Pool.ExportBackup(Fixture.User(1),backup.Id)){var imported=await f.Pool.ImportBackup(Fixture.User(1),input,default);Assert.Equal(backup.Name,imported.Name);}
        f.Pool.RestoreBackup(Fixture.User(1),backup.Id,new(Guid.NewGuid(),"Restored"));await f.Drain(1);
        var restored=f.Pool.Snapshot(Fixture.User(1)).Instances.Single(i=>i.Name=="Restored");Assert.Equal("secret",restored.Password);Assert.Equal("3816653940",restored.Mods);Assert.Equal("FlightSchool",restored.StartLayer);Assert.Equal(1,f.Infra.RestoredFiles);Assert.Equal("Group=Owner:chat,kick",f.Infra.Configurations[(restored.Id,"Admins.cfg")]);Assert.Equal(10,f.Pool.Snapshot(Fixture.User(1)).Instances.SelectMany(i=>i.Ports.All).Distinct().Count());Assert.Equal(1,f.Infra.Rents);
    }
    [Fact] public async Task BackupsArePrivateSurviveHostLossAndCannotCreateRental()
    {
        using var f=new Fixture();f.Infra.ArchiveData=Tar();f.Start(1);await f.Drain(1);var backup=await f.Pool.CreateBackup(Fixture.User(1),Id(f),new("Stored"),default);
        Assert.Empty(f.Pool.Tools(Fixture.User(2),null).Backups);Assert.Throws<UnauthorizedAccessException>(()=>f.Pool.ExportBackup(Fixture.User(2),backup.Id));Assert.Throws<UnauthorizedAccessException>(()=>f.Pool.DeleteBackup(Fixture.User(2),backup.Id));
        f.Infra.Host=null;await f.Pool.Reconcile(default);Assert.Single(new PoolCoordinator(f.Store,f.Infra).Tools(Fixture.User(1),null).Backups);Assert.Throws<InvalidOperationException>(()=>f.Pool.RestoreBackup(Fixture.User(1),backup.Id,new(Guid.NewGuid(),"Restored")));Assert.Equal(1,f.Infra.Rents);
    }
    [Fact] public async Task CloneIsPrivateIdempotentAndUsesDistinctPorts()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);var id=Id(f);var request=new CopyRequest(Guid.NewGuid(),"Clone");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Pool.Clone(Fixture.User(2),id,request,default));var first=await f.Pool.Clone(Fixture.User(1),id,request,default);Assert.Equal(first.Id,(await f.Pool.Clone(Fixture.User(1),id,request,default)).Id);await f.Drain(1);Assert.Equal(10,f.Pool.Snapshot(Fixture.User(1)).Instances.SelectMany(i=>i.Ports.All).Distinct().Count());Assert.Equal(1,f.Infra.Rents);
    }
    [Fact] public async Task QueuedCloneCannotRentIfOriginalHostDisappears()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);await f.Pool.Clone(Fixture.User(1),Id(f),new(Guid.NewGuid(),"Clone"),default);var state=f.Store.Load<PoolState>("pool")!;state.HostId=null;state.HostPhase="empty";f.Store.Save("pool",state);f.Infra.Host=null;var restored=new PoolCoordinator(f.Store,f.Infra);await restored.ProcessNext(default);Assert.Equal(1,f.Infra.Rents);Assert.Contains(f.Store.Load<PoolState>("pool")!.Operations,o=>o.Phase=="failed");
    }
    [Theory][InlineData("Logs/../../shadow",TarEntryType.RegularFile)][InlineData("OSI_Scoreboards/link",TarEntryType.SymbolicLink)][InlineData("Server.cfg",TarEntryType.RegularFile)]
    public void ImportedFileEscapesAndLinksAreRejected(string path,TarEntryType type)
    {using var input=new MemoryStream(Tar(path,type));Assert.Throws<ArgumentException>(()=>BackupFormat.ValidateFiles(input));}
    [Fact] public async Task OverlargeInputIsRejectedBeforeSaving()
    {using var input=new MemoryStream(new byte[100]);using var output=new MemoryStream();await Assert.ThrowsAsync<ArgumentException>(()=>BackupFormat.Copy(input,output,50,default));}
    [Fact] public void SystemConfigsCannotBeImported()
    {
        using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)){using(var entry=zip.CreateEntry("manifest.json").Open())JsonSerializer.Serialize(entry,new BackupManifest(1,"Unsafe",DateTimeOffset.UtcNow,new("Server","","",100),new(){["Rcon.cfg"]="Password=bad"}));zip.CreateEntry("files.tar.gz");}memory.Position=0;using var read=new ZipArchive(memory);Assert.Throws<ArgumentException>(()=>BackupFormat.ReadManifest(read));
    }
    [Fact] public async Task MetricsAndMatchFilesArePrivateAndPersist()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);f.Infra.ServerFiles=[new("OSI_Scoreboards/match.json",22)];await f.Pool.TickServerTools(default);
        var own=f.Pool.Tools(Fixture.User(1),Id(f));Assert.Single(own.Metrics);Assert.Equal(12,own.Metrics[0].CpuPercent);Assert.Single(own.Matches);Assert.Empty(f.Pool.Tools(Fixture.User(2),null).Matches);Assert.Throws<UnauthorizedAccessException>(()=>f.Pool.Tools(Fixture.User(2),Id(f)));Assert.Single(new PoolCoordinator(f.Store,f.Infra).Tools(Fixture.User(1),Id(f)).Metrics);
    }
    [Fact] public void RoundParserUsesActualSquadEventsPersistsAcrossBatchesAndDeduplicates()
    {
        var progress=new RoundObservation();var start="[2026.10.12-10.00.00:001][ 0]LogWorld: Bringing World /Squad/Maps/AlBasrah/AlBasrah_AAS_v1.AlBasrah_AAS_v1";
        Assert.Empty(MatchHistory.Parse("one",start,progress));
        var ended="[2026.10.12-10.30.00:001][ 0]LogSquadTrace: [DedicatedServer]ASQGameMode::DetermineMatchWinner(): The game was a draw on AlBasrah_AAS_v1\n[2026.10.12-10.30.00:001][ 0]LogSquadTrace: [DedicatedServer]ASQGameMode::DetermineMatchWinner(): USA won on AlBasrah_AAS_v1\n[2026.10.12-10.30.00:002][ 0]LogGameState: Match State Changed from InProgress to WaitingPostMatch";
        var match=Assert.Single(MatchHistory.Parse("one",ended,progress));Assert.Equal("USA",match.Result);Assert.Equal("AlBasrah_AAS_v1",match.Layer);Assert.InRange(match.DurationSeconds!.Value,1800,1801);Assert.Empty(MatchHistory.Parse("one",start+"\n"+ended,progress));
    }
    [Theory][InlineData("https://127.0.0.1/api/webhooks/123/x")][InlineData("https://discord.com.evil/api/webhooks/123/x")][InlineData("http://discord.com/api/webhooks/123/x")][InlineData("https://discord.com/api/webhooks/123/x?foo=bar")]
    public void WebhookCannotTargetOtherHosts(string url)=>Assert.Throws<ArgumentException>(()=>DiscordNotifications.Validate(url));
    private sealed class Transport:HttpMessageHandler
    {
        public string? Body;
        public int Calls;
        public HttpStatusCode Status=HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Calls++;Body=await r.Content!.ReadAsStringAsync(ct);return new(Status);}
    }
    [Fact] public async Task DiscordPayloadDisablesMentionsAndNeverContainsCredentials()
    {
        var handler=new Transport();using var client=new HttpClient(handler);await DiscordNotifications.Send("https://discord.com/api/webhooks/12345678901234567/abcdefghijklmnopqrstuvwxyz123456","Server @everyone",default,client);using var doc=JsonDocument.Parse(handler.Body!);Assert.Empty(doc.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
    }
    [Fact] public async Task DiscordReportsReadyHostWithoutGameEventsOnlyOnce()
    {
        using var f=new Fixture();var handler=new Transport();using var client=new HttpClient(handler);
        f.Pool.SetDiscord(Fixture.User(1),new("https://discord.com/api/webhooks/12345678901234567/abcdefghijklmnopqrstuvwxyz123456",true,"ru"));f.Start(1);await f.Drain(1);
        await f.Pool.DeliverDiscord(default,client);Assert.Equal(1,handler.Calls);Assert.Contains("Хост готов",JsonDocument.Parse(handler.Body!).RootElement.GetProperty("content").GetString());
        await new PoolCoordinator(f.Store,f.Infra).DeliverDiscord(default,client);Assert.Equal(1,handler.Calls);
    }
    [Fact] public async Task SavingDiscordSettingsDoesNotDiscardPendingEvent()
    {
        using var f=new Fixture();var handler=new Transport();using var client=new HttpClient(handler);
        f.Pool.SetDiscord(Fixture.User(1),new("https://discord.com/api/webhooks/12345678901234567/abcdefghijklmnopqrstuvwxyz123456",true,"ru"));f.Start(1);await f.Drain(1);f.Infra.GameStatus=new("gameReady");await f.Pool.Reconcile(default);
        f.Pool.SetDiscord(Fixture.User(1),new(null,true,"ru"));await f.Pool.DeliverDiscord(default,client);
        Assert.Equal(1,handler.Calls);Assert.Contains("Сервер готов",JsonDocument.Parse(handler.Body!).RootElement.GetProperty("content").GetString());
    }
    [Fact] public void DiscordSecretsAreNotReturnedToClients()
    {
        using var f=new Fixture();var secret="https://discord.com/api/webhooks/12345678901234567/abcdefghijklmnopqrstuvwxyz123456";f.Pool.SetDiscord(Fixture.User(1),new(secret,true,"ru"));var view=f.Pool.Tools(Fixture.User(1),null);Assert.True(view.Discord.Configured);Assert.DoesNotContain(secret,JsonSerializer.Serialize(view));Assert.False(f.Pool.Tools(Fixture.User(2),null).Discord.Configured);f.Pool.SetDiscord(Fixture.User(1),new("",false,"ru"));Assert.False(f.Pool.Tools(Fixture.User(1),null).Discord.Configured);
    }
    [Fact] public async Task DiscordDeliversPrivateEventsOnceAndRemembersAcrossRestart()
    {
        using var f=new Fixture();var handler=new Transport();using var client=new HttpClient(handler);f.Pool.SetDiscord(Fixture.User(1),new("https://discord.com/api/webhooks/12345678901234567/abcdefghijklmnopqrstuvwxyz123456",true,"ru"));f.Start(1);await f.Drain(1);f.Infra.GameStatus=new("gameReady");await f.Pool.Reconcile(default);
        await f.Pool.DeliverDiscord(default,client);Assert.Equal(1,handler.Calls);Assert.Contains("Сервер готов",JsonDocument.Parse(handler.Body!).RootElement.GetProperty("content").GetString());await new PoolCoordinator(f.Store,f.Infra).DeliverDiscord(default,client);Assert.Equal(1,handler.Calls);Assert.NotNull(f.Pool.Tools(Fixture.User(1),null).Discord.LastDelivered);
    }
    [Fact] public async Task DiscordFailuresBackOffAndDoNotInterruptServerOperations()
    {
        using var f=new Fixture();var handler=new Transport{Status=HttpStatusCode.TooManyRequests};using var client=new HttpClient(handler);f.Pool.SetDiscord(Fixture.User(1),new("https://discord.com/api/webhooks/12345678901234567/abcdefghijklmnopqrstuvwxyz123456",true,"ru"));f.Start(1);await f.Drain(1);f.Infra.GameStatus=new("gameReady");await f.Pool.Reconcile(default);await f.Pool.DeliverDiscord(default,client);await f.Pool.DeliverDiscord(default,client);Assert.Equal(1,handler.Calls);Assert.Equal("Discord: HTTP 429",f.Pool.Tools(Fixture.User(1),null).Discord.LastError);Assert.Equal("running",f.Pool.Snapshot(Fixture.User(1)).Instances.Single().Phase);
    }
}


