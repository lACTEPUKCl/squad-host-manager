namespace SquadHostManager.Core;

public record AccessPolicy(DateTimeOffset? ExpiresAt=null,int MaxServers=16,bool CanRent=false);
public record TemplateView(string Id,string Name,ServerSettingsRequest Settings,Dictionary<string,string> Configs);
public record TemplateRequest(string Name,string InstanceId);
public record TemplateStart(Guid RequestId);
public record BudgetPolicy(decimal Amount=0,string Currency="EUR",bool Sound=false);
public record ConfigVersion(string Id,string InstanceId,string File,DateTimeOffset At,string Content);
public record Notice(string Id,DateTimeOffset At,string Code,string? InstanceId,string Detail,bool Read=false);
public record AutomationView(List<TemplateView> Templates,List<Notice> Notices,BudgetPolicy Budget,List<CostUserRow> Costs,decimal? HourlyForecast,string? ForecastCurrency=null,bool HistoryIncomplete=false,decimal? HostSpent=null);
public record Diagnostic(string Code,string Detail,string Action);

public static class StartupDiagnosis
{
    public static Diagnostic Evaluate(string phase,string? gameState,string? error,string logs)
    {
        var text=(error+"\n"+logs).ToLowerInvariant();
        if(text.Contains("invalid world")||text.Contains("failed to load map")||text.Contains("failed to load package"))return new("diagLayer",error??"","chooseLayer");
        if(text.Contains("address already in use")||text.Contains("bind failed")||text.Contains("port collision"))return new("diagPorts",error??"","retry");
        if(text.Contains("workshop")||text.Contains("uplugin")||text.Contains("mod download"))return new("diagMod",error??"","mods");
        if(text.Contains("permission denied")||text.Contains("authentication failed"))return new("diagSsh",error??"","connectionSettings");
        if(text.Contains("no space left"))return new("diagDisk",error??"","files");
        if(text.Contains("critical error")||text.Contains("segmentation fault")||gameState=="gameFailed")return new("diagCrash",error??"","console");
        if(gameState=="gameReady")return new("gameReady","","connectGame");
        if(phase is "queued" or "starting" or "restarting" || gameState is "gameDownloading" or "gameLoading")return new("diagPreparing",error??"","console");
        return new("diagUnknown",error??"","console");
    }
}
