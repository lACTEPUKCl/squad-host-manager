namespace SquadHostManager.Core;

public sealed class LocationAvailabilityWatch
{
    private readonly Dictionary<string,int> previous=new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Selected {get;}=new(StringComparer.OrdinalIgnoreCase);
    public List<RentalLocation> Observe(IEnumerable<RentalLocation> locations)
    {
        var ready=new List<RentalLocation>();
        foreach(var location in locations)
        {
            if(Selected.Contains(location.Slug) && previous.TryGetValue(location.Slug,out var old) && old==0 && location.Available>0)ready.Add(location);
            previous[location.Slug]=Math.Max(0,location.Available);
        }
        return ready;
    }
    public void Reset(){previous.Clear();Selected.Clear();}
}
