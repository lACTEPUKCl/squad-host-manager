using SquadHostManager.Core;
namespace SquadHostManager.Service;
public sealed class HostingInfrastructure(CherryInfrastructure cherry,RespawnInfrastructure respawn,Store store,string? provider=null):IInfrastructure
{
    private IInfrastructure Selected=>(provider??store.Load<PoolState>("pool")?.Provider)=="respawn"?respawn:cherry;
    private IInfrastructure For(string host)=>host.StartsWith(RespawnInfrastructure.Prefix,StringComparison.Ordinal)?respawn:cherry;
    public Task<BalanceView> Balance(CancellationToken ct)=>Selected.Balance(ct);
    public Task<List<RentalLocation>> Locations(CancellationToken ct)=>Selected.Locations(ct);
    public Task<RemoteHost?> FindHost(string marker,CancellationToken ct)=>Selected.FindHost(marker,ct);
    public Task<RemoteHost> Rent(string marker,CancellationToken ct)=>Selected.Rent(marker,ct);
    public Task<RemoteHost?> GetHost(string id,CancellationToken ct)=>For(id).GetHost(id,ct);
    public Task Prepare(string host,string ip,CancellationToken ct)=>For(host).Prepare(host,ip,ct);
    public Task Prepare(string host,string ip,Action<string> progress,CancellationToken ct)=>For(host).Prepare(host,ip,progress,ct);
    public Task<GameStatus?> PreparationStatus(string host,string ip,CancellationToken ct)=>For(host).PreparationStatus(host,ip,ct);
    public Task<List<string>> CpuSets(string host,string ip,CancellationToken ct)=>For(host).CpuSets(host,ip,ct);
    public Task<List<RemoteInstance>> Inventory(string host,string ip,CancellationToken ct)=>For(host).Inventory(host,ip,ct);
    public Task<HashSet<int>> BoundPorts(string host,string ip,CancellationToken ct)=>For(host).BoundPorts(host,ip,ct);
    public Task Start(string host,string ip,Instance instance,CancellationToken ct)=>For(host).Start(host,ip,instance,ct);
    public Task Update(string host,string ip,Instance instance,CancellationToken ct)=>For(host).Update(host,ip,instance,ct);
    public Task Stop(string host,string ip,string id,CancellationToken ct)=>For(host).Stop(host,ip,id,ct);
    public Task Delete(string id,CancellationToken ct)=>For(id).Delete(id,ct);
    public Task<string> Logs(string host,string ip,string id,CancellationToken ct)=>For(host).Logs(host,ip,id,ct);
    public Task<string> Command(string host,string ip,Instance instance,string command,CancellationToken ct)=>For(host).Command(host,ip,instance,command,ct);
    public Task<string> Config(string host,string ip,string id,ConfigRequest request,CancellationToken ct)=>For(host).Config(host,ip,id,request,ct);
    public Task<Stream> ArchiveStream(string host,string ip,string id,CancellationToken ct)=>For(host).ArchiveStream(host,ip,id,ct);
    public Task<Stream> BackupArchiveStream(string host,string ip,string id,CancellationToken ct)=>For(host).BackupArchiveStream(host,ip,id,ct);
    public Task<byte[]> Archive(string host,string ip,string id,CancellationToken ct)=>For(host).Archive(host,ip,id,ct);
    public Task<List<ServerFile>> Files(string host,string ip,string id,CancellationToken ct)=>For(host).Files(host,ip,id,ct);
    public Task<byte[]> Download(string host,string ip,string id,string path,CancellationToken ct)=>For(host).Download(host,ip,id,path,ct);
    public Task<GameStatus?> Probe(string host,string ip,Instance instance,CancellationToken ct)=>For(host).Probe(host,ip,instance,ct);
    public Task<MetricSample> Metrics(string host,string ip,Instance instance,CancellationToken ct)=>For(host).Metrics(host,ip,instance,ct);
    public Task<LogBatch> MatchLogs(string host,string ip,Instance instance,CancellationToken ct)=>For(host).MatchLogs(host,ip,instance,ct);
    public Task RestoreFiles(string host,string ip,string id,string localArchive,CancellationToken ct)=>For(host).RestoreFiles(host,ip,id,localArchive,ct);
    public Task<Dictionary<string,string>> DownloadedConfigs(string host,string ip,CancellationToken ct)=>For(host).DownloadedConfigs(host,ip,ct);
}
