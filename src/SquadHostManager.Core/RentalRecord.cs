using System.Globalization;
using System.Text.Json;

namespace SquadHostManager.Core;

public sealed record RateSample(DateTimeOffset At, decimal Rate);
public sealed record RentalRecord
{
    public RentalRecord Copy() => this with { Rates = Rates.ToList() };
    public bool EstimateIncomplete { get; set; }
    public string Id { get; set; } = "";
    public string? Model { get; set; }
    public string? Location { get; set; }
    public string? Ip { get; set; }
    public int? Cores { get; set; }
    public string? Memory { get; set; }
    public string? Storage { get; set; }
    public string? Currency { get; set; }
    public string? BillingUnit { get; set; }
    public decimal? HourlyRate { get; set; }
    public decimal? ProviderAmount { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? ProviderStatus { get; set; }
    public string State { get; set; } = "unknown";
    public List<RateSample> Rates { get; set; } = [];
    public bool EndTimeEstimated { get; set; }
    public decimal? Estimate(DateTimeOffset now)
    {
        if (EstimateIncomplete || StartedAt == null || Rates.Count == 0) return null;
        decimal result = 0; var end = EndedAt ?? now;
        for (var i=0;i<Rates.Count;i++)
        {
            var until=i+1<Rates.Count && Rates[i+1].At<end?Rates[i+1].At:end;
            if(until>Rates[i].At) result+=(decimal)(until-Rates[i].At).TotalHours*Rates[i].Rate;
        }
        return decimal.Round(result,4);
    }
    public static RentalRecord FromCherry(JsonElement data, DateTimeOffset checkedAt)
    {
        JsonElement Obj(JsonElement o,string key)=>o.ValueKind==JsonValueKind.Object && o.TryGetProperty(key,out var v)?v:default;
        string? Text(JsonElement o,string key){var v=Obj(o,key);return v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null?null:v.ToString();}
        decimal? Number(JsonElement o,string key)=>decimal.TryParse(Text(o,key),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)?n:null;
        var plan=Obj(data,"plan");var specs=Obj(plan,"specs");var pricing=Obj(data,"pricing");var region=Obj(data,"region");
        var unit=Text(pricing,"unit");var hourly=unit?.ToLowerInvariant() is "hours" or "hour" or "hourly";
        var rate=hourly?Number(pricing,"unit_price"):null;
        var total=Number(pricing,"price_total");
        if(rate<0)rate=null;
        var cpu=Obj(specs,"cpus");var memory=Obj(specs,"memory");var storage=Obj(specs,"storage");
        return new()
        {
            Id=Text(data,"id")??throw new ArgumentException("Cherry server ID missing."),
            Model=Text(plan,"title")??Text(plan,"name"),Location=Text(region,"name")??Text(region,"location"),Ip=Text(data,"ip"),
            Cores=(int?)Number(cpu,"cores"),Memory=Text(memory,"total") is {} mem?mem+" "+Text(memory,"unit"):null,
            Storage=storage.ValueKind==JsonValueKind.Array?string.Join(", ",storage.EnumerateArray().Select(d=>Text(d,"count")+" × "+Text(d,"name"))):null,
            Currency=Text(pricing,"currency"),BillingUnit=unit,HourlyRate=rate,
            ProviderAmount=Number(pricing,"billable_amount")??total,
            StartedAt=DateTimeOffset.TryParse(Text(data,"created_at"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date)?date:null,
            CheckedAt=checkedAt,ProviderStatus=Text(data,"status"),State=(Text(data,"state")??Text(data,"status")??"unknown").ToLowerInvariant()
        };
    }
}
