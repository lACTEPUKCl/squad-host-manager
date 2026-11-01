namespace SquadHostManager.Core;
public record HostingProviderRequest(string Provider);
public record HostingProviderView(string Selected,bool CanSwitch,bool CherryRented);
public record RespawnTokenRequest(string Token);
public record RespawnPlan(int Id,int MemoryMb,decimal HourlyPrice)
{
    public string Display=>$"{MemoryMb/1024} GB · {HourlyPrice:0.0000} EUR / h";
}
public record RespawnServer(string Id,string Name,string State,string? Address=null);
public record RespawnRentRequest(Guid RequestId,int PlanId,decimal HourlyPrice,string Region,ServerSettingsRequest? Settings=null);
public record RespawnView(bool Configured,List<RespawnPlan> Plans,List<RespawnServer> Servers,bool PendingOrder=false);
