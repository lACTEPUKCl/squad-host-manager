using SquadHostManager.Core;

namespace SquadHostManager.Service;

public sealed class RentalRejectedException(string message) : InvalidOperationException(message);

public sealed class PoolState
{
    public List<OwnedBackup> Backups {get;set;}=[];
    public HashSet<string> DeletedMatches {get;set;}=[];
    public List<OwnedMatch> Matches {get;set;}=[];
    public Dictionary<string,List<MetricSample>> Metrics {get;set;}=[];
    public Dictionary<string,RoundObservation> Rounds {get;set;}=[];
    public Dictionary<string,DiscordDelivery> Discord {get;set;}=[];
    public List<OwnedTemplate> Templates { get; set; }=[];
    public List<OwnedNotice> Notices { get; set; }=[];
    public List<OwnedVersion> Versions { get; set; }=[];
    public Dictionary<string,BudgetPolicy> Budgets { get; set; }=[];

    public List<CostParticipation> CostEvents { get; set; } = [];
    public int CpuCapacity { get; set; } = 16;
    public HostStartupView? Startup { get; set; }
    public List<RentalRecord> Rentals { get; set; } = [];
    public DateTimeOffset? HostCheckedAt { get; set; }
    public bool HostReachable { get; set; }
    public string? RentedByName { get; set; }
    public string? ConnectedByName { get; set; }
    public string Marker { get; set; } = "shm-" + Guid.NewGuid().ToString("N")[..16];
    public string HostPhase { get; set; } = "empty";
    public string? HostId { get; set; }
    public string? Ip { get; set; }
    public string? HostError { get; set; }
    public string? LastStoppedBy { get; set; }
    public string? LastEvent { get; set; }
    public List<Instance> Instances { get; set; } = [];
    public List<Operation> Operations { get; set; } = [];
}
public sealed class Instance
{
    public Dictionary<string,LogPosition> LogPositions {get;set;}=[];
    public string? RestoreBackupId {get;set;}
    public List<DateTimeOffset> RecoveryAttempts { get; set; }=[];
    public Dictionary<string,string>? CommonConfigs {get;set;}
    public string? ServerConfig {get;set;}
    public Dictionary<string,string>? TemplateConfigs { get; set; }
    public bool RecoveryNeeded {get;set;}
    public string? LastArchiveId {get;set;}
    public DateTimeOffset? ArchivedStartedAt {get;set;}
    public bool ReadyNotified { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public string? GameState { get; set; }
    public double? DownloadPercent { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string Name { get; set; } = "";
    public string? CpuSet { get; set; }
    public int Slot { get; set; }
    public PortSet Ports { get; set; } = PortSet.ForSlot(0);
    public string Phase { get; set; } = "queued";
    public string? Error { get; set; }
    public string Password { get; set; } = "";
    public string RconPassword { get; set; } = Store.Secret();
    public string? ProgressKey { get; set; }
    public string StartLayer { get; set; } = "";
    public string Mods { get; set; } = "";
    public int MaxPlayers { get; set; }
}
public sealed class Operation
{
    public string? ExpectedHostId {get;set;}
    public string? RenterName { get; set; }
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Release { get; set; }
    public string Phase { get; set; } = "queued";
    public string? Error { get; set; }
}
public record RemoteHost(string Id, string? Ip, bool Ready, RentalRecord? Rental = null);
public record RemoteInstance(string Id, bool Running, int[] Ports);
public record GameStatus(string State, double? DownloadPercent = null, string? ProgressKey = null);
public record LogPosition(long Offset,long Inode);
public record LogBatch(string Text,Dictionary<string,LogPosition> Positions);
public interface IInfrastructure
{
    Task<Dictionary<string,string>> DownloadedConfigs(string hostId,string ip,CancellationToken ct)=>Task.FromResult(new Dictionary<string,string>());
    Task<BalanceView> Balance(CancellationToken ct)=>Task.FromResult(new BalanceView(null,"EUR",null,null,true));
    async Task<LogBatch> MatchLogs(string hostId,string ip,Instance instance,CancellationToken ct)=>new(await Logs(hostId,ip,instance.Id,ct),instance.LogPositions);
    Task<MetricSample> Metrics(string hostId,string ip,Instance instance,CancellationToken ct)=>Task.FromResult(new MetricSample(DateTimeOffset.UtcNow,null,null,null,null,null,null,null,"unavailable"));
    Task RestoreFiles(string hostId,string ip,string instanceId,string localArchive,CancellationToken ct)=>throw new NotSupportedException();
    Task<List<RentalLocation>> Locations(CancellationToken ct) => Task.FromResult(new List<RentalLocation>());
    Task<RemoteHost?> FindHost(string marker, CancellationToken ct);
    Task<RemoteHost> Rent(string marker, CancellationToken ct);
    Task<RemoteHost?> GetHost(string id, CancellationToken ct);
    Task Prepare(string hostId, string ip, CancellationToken ct);
    Task Prepare(string hostId, string ip, Action<string> progress, CancellationToken ct) => Prepare(hostId, ip, ct);
    Task<GameStatus?> PreparationStatus(string hostId,string ip,CancellationToken ct) => Task.FromResult<GameStatus?>(null);
    Task<List<string>> CpuSets(string hostId, string ip, CancellationToken ct) => Task.FromResult(Enumerable.Range(0,16).Select(i=>$"{i},{i+16}").ToList());
    Task<List<RemoteInstance>> Inventory(string hostId, string ip, CancellationToken ct);
    Task<HashSet<int>> BoundPorts(string hostId, string ip, CancellationToken ct);
    Task Start(string hostId, string ip, Instance instance, CancellationToken ct);
    Task Stop(string hostId, string ip, string instanceId, CancellationToken ct);
    Task Delete(string hostId, CancellationToken ct);
    Task<string> Logs(string hostId, string ip, string instanceId, CancellationToken ct);
    Task<string> Command(string hostId, string ip, Instance instance, string command, CancellationToken ct);
    Task<string> Config(string hostId, string ip, string instanceId, ConfigRequest request, CancellationToken ct);
    async Task<Stream> ArchiveStream(string hostId,string ip,string instanceId,CancellationToken ct) => new MemoryStream(await Archive(hostId,ip,instanceId,ct),false);
    Task<Stream> BackupArchiveStream(string hostId,string ip,string instanceId,CancellationToken ct)=>ArchiveStream(hostId,ip,instanceId,ct);
    Task<byte[]> Archive(string hostId, string ip, string instanceId, CancellationToken ct);
    Task<List<ServerFile>> Files(string hostId, string ip, string instanceId, CancellationToken ct) => Task.FromResult(new List<ServerFile>());
    Task<byte[]> Download(string hostId, string ip, string instanceId, string path, CancellationToken ct) => throw new NotSupportedException();
    Task<GameStatus?> Probe(string hostId, string ip, Instance instance, CancellationToken ct) => Task.FromResult<GameStatus?>(null);
}

public record OwnedTemplate(string OwnerId,TemplateView Value);
public record OwnedBackup(string OwnerId,BackupView Value);
public record OwnedMatch(string OwnerId,MatchRecord Value);
public sealed class RoundObservation
{
    public DateTimeOffset LastAt {get;set;}
    public DateTimeOffset? StartedAt {get;set;}
    public string? Layer {get;set;}
    public string? Result {get;set;}
    public List<string> Seen {get;set;}=[];
}
public sealed class DiscordDelivery
{
    public string Webhook {get;set;}="";
    public bool Enabled {get;set;}
    public string Language {get;set;}="ru";
    public string? LastError {get;set;}
    public DateTimeOffset? LastDelivered {get;set;}
    public List<string> Delivered {get;set;}=[];
    public DateTimeOffset RetryAt {get;set;}
}
public record OwnedNotice(string OwnerId,Notice Value);
public record OwnedVersion(string OwnerId,ConfigVersion Value);

