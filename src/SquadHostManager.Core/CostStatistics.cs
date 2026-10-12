namespace SquadHostManager.Core;

public record CostParticipant(string Id, string Name);
public record CostParticipation(DateTimeOffset At, string HostId, List<CostParticipant> Users);
public record CostUserRow(string UserId, string Name, string Currency, decimal Spent, decimal SeparateHosts, decimal Saved);
public record CostHour(DateTimeOffset At, string HostId, string Currency, decimal Rate, List<CostParticipant> Users);
public record CostStatistics(List<CostUserRow> Users, List<CostHour> Hours, bool HistoryIncomplete);

public static class CostAccounting
{
    // Each prepaid hour is divided across everyone who participated in that hour.
    // Recalculation is deterministic; reading a report never creates a new charge.
    public static CostStatistics Calculate(IEnumerable<RentalRecord> rentals, IEnumerable<CostParticipation> events, DateTimeOffset now, DateTimeOffset? from = null)
    {
        var history=events.Where(e=>e.At<=now).OrderBy(e=>e.At).ToList(); var hours=new List<CostHour>(); var incomplete=false;
        foreach(var rental in rentals)
        {
            if(rental.StartedAt is not {} start || rental.Currency is not {} currency) { incomplete=true; continue; }
            var end=rental.EndedAt??now; var changes=history.Where(e=>e.HostId==rental.Id).ToList();
            if(changes.Count==0 || changes[0].At>start.AddSeconds(30)) incomplete=true;
            for(var at=start;at<end;at=at.AddHours(1))
            {
                if(from is {} since && at.AddHours(1)<=since)continue;
                var until=at.AddHours(1)<end?at.AddHours(1):end;
                var current=changes.LastOrDefault(e=>e.At<=at);
                var users=(current?.Users??[]).Concat(changes.Where(e=>e.At>at && e.At<until).SelectMany(e=>e.Users)).DistinctBy(u=>u.Id).OrderBy(u=>u.Id).ToList();
                var rate=rental.Rates.LastOrDefault(r=>r.At<=at)?.Rate??rental.HourlyRate;
                if(rate==null || users.Count==0) { incomplete=true; continue; }
                hours.Add(new(at,rental.Id,currency,rate.Value,users));
            }
        }
        var totals=new Dictionary<(string,string),(string Name,decimal Spent,decimal Separate)>();
        foreach(var hour in hours)
        {
            // Assign the rounding remainder deterministically so shares sum exactly.
            var share=decimal.Round(hour.Rate/hour.Users.Count,6); decimal allocated=0;
            for(var i=0;i<hour.Users.Count;i++)
            {
                var user=hour.Users[i];var cost=i==hour.Users.Count-1?hour.Rate-allocated:share;allocated+=cost;
                var key=(user.Id,hour.Currency);var old=totals.GetValueOrDefault(key);
                totals[key]=(user.Name,old.Spent+cost,old.Separate+hour.Rate);
            }
        }
        return new(totals.Select(p=>new CostUserRow(p.Key.Item1,p.Value.Name,p.Key.Item2,p.Value.Spent,p.Value.Separate,p.Value.Separate-p.Value.Spent)).OrderBy(r=>r.Name).ToList(),hours.OrderByDescending(h=>h.At).ToList(),incomplete);
    }
}
