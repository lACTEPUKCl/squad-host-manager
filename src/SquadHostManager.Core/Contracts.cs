namespace SquadHostManager.Core;

public static class Product
{
    public const string Repository = "lACTEPUKCl/squad-host-manager";
    public const string SupportUrl = "https://buymeacoffee.com/flightschoolsquad";
    public const string ReferralUrl = "https://www.cherryservers.com/?affiliate=ZQ6937K6";
}
public record RegisterRequest(string Name, string Password, string Invite);
public record LoginRequest(string Name, string Password);
public record KeyLoginRequest(string Key);
public record CreateUserRequest(string Name);
public record UserAccessView(string Id, string Name, bool IsAdmin, bool HasKey, bool Disabled,DateTimeOffset? ExpiresAt=null,int MaxServers=16,bool CanRent=false);
public record AccessGrant(UserAccessView User, string Key);
public record SessionResponse(string Token, string UserId, string Name, bool IsAdmin);
public record StartRequest(Guid RequestId, string Name, string? Password, string Mods, int MaxPlayers = 100, string? InstanceId = null, string? StartLayer = null);
public record StopRequest(Guid RequestId, bool ReleaseIfLast = false, string? InstanceId = null);
public record ServerSettingsRequest(string Name, string? Password, string Mods, int MaxPlayers, string? StartLayer = null);
public record CommandRequest(string Command);
public record RestartRequest(Guid RequestId);
public record InviteRequest(int Uses = 1);
public record HostView(string? Id, string? Ip, string Phase, string? Error, string? RentedByName = null, string? ConnectedByName = null, RentalRecord? Rental = null, bool Verified = false, HostStartupView? Startup = null);
public record InstanceView(string Id, string OwnerId, string OwnerName, string Name, int Slot,
    PortSet Ports, string Phase, bool IsMine, string? Error, string? GameState = null, double? DownloadPercent = null, int MaxPlayers = 100, string Mods = "", DateTimeOffset? StartedAt = null, int? QueuePosition = null, string? Password = null, string StartLayer = "", string? ProgressKey = null);
public record PoolView(HostView Host, List<InstanceView> Instances, bool CanRelease, bool Busy, string? LastEvent, bool CanStart = true, bool ReleaseEnabled = true, List<RentalRecord>? RentalHistory = null, int CpuCapacity = 16,int? OccupiedServers=null,int? OtherActiveServers=null,int? OtherStoppingServers=null,bool CanRent=false);
public record ConfigRequest(string File, string? Content = null);
public record ServerFile(string Path, long Bytes);
public record OperationView(string Id, string Kind, string Phase, string? Error);
public record PortSet(int Game, int Query, int Rcon, int Beacon)
{
    public int[] All => [Game, Game + 1, Query, Rcon, Beacon];
    public static PortSet ForSlot(int slot)
    {
        if (slot is < 0 or >= 16) throw new ArgumentOutOfRangeException(nameof(slot));
        // Disjoint ranges; reserve the secondary Steam UDP port too.
        return new(7787 + slot * 2, 27165 + slot, 21114 + slot, 15000 + slot);
    }
    public static int Allocate(IEnumerable<int> slots, IEnumerable<int> externalPorts, int capacity = 16)
    {
        var taken = slots.ToHashSet();
        var ports = externalPorts.ToHashSet();
        for (var i = 0; i < Math.Min(capacity, 16); i++)
            if (!taken.Contains(i) && !ForSlot(i).All.Any(ports.Contains)) return i;
        throw new InvalidOperationException("На хосте нет свободного места с уникальными портами.");
    }
}

public record RentalLocation(string Slug, string Name, int Available, decimal? HourlyPrice, string? Currency, int? PrebuiltId = null, int? Cores = null, int? Threads = null, string? Memory = null, string? Storage = null, string? Network = null, string? Traffic = null);
public record RentalLocationRequest(string Region);

public record RentHostRequest(Guid RequestId, string Region, decimal HourlyPrice, string Currency, int PrebuiltId);

public record HostStageEvent(DateTimeOffset At, string Stage);
public record HostStartupView(string Stage, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, string? ProviderState, List<HostStageEvent> Events, string? Error = null, double? DownloadPercent = null);

public record ServerDefaults(string Mods="",string? Admins=null);
