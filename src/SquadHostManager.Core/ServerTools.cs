namespace SquadHostManager.Core;

public record BackupView(string Id,string Name,string ServerName,DateTimeOffset At,long Bytes);
public record BackupManifest(int Format,string Name,DateTimeOffset At,ServerSettingsRequest Settings,Dictionary<string,string> Configs);
public record CopyRequest(Guid RequestId,string Name);
public record BackupRequest(string Name);
public record MetricSample(DateTimeOffset At,double? CpuPercent,long? MemoryBytes,long? MemoryLimit,long? DiskBytes,long? HostDiskAvailable,int? Players,string? Layer,string? Error=null);
public record MatchRecord(string Id,string InstanceId,DateTimeOffset At,string? Layer,string? Result,double? DurationSeconds,List<ServerFile> Files,string? ServerName=null,string? ArchiveId=null);
public record ServerToolsView(List<BackupView> Backups,List<MetricSample> Metrics,List<MatchRecord> Matches,DiscordStatus Discord);
public record DiscordSettings(string? Webhook=null,bool Enabled=false,string Language="ru");
public record DiscordStatus(bool Configured,bool Enabled,string Language,string? LastError=null,DateTimeOffset? LastDelivered=null);
