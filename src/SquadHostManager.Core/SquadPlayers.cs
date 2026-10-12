using System.Text.RegularExpressions;

namespace SquadHostManager.Core;

public record SquadPlayer(string Id, string Name, string Team, string Squad, string SteamId, bool IsLeader = false);
public static class SquadPlayers
{
    public static List<SquadPlayer> Parse(string output)
    {
        var result = new List<SquadPlayer>(); var active = false;
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("----- Active Players", StringComparison.Ordinal)) { active = true; continue; }
            if (line.Contains("----- Recently Disconnected", StringComparison.Ordinal)) { active = false; continue; }
            if (!active) continue;
            var id = Regex.Match(line, @"\bID:\s*(\d+)\b"); var name = Regex.Match(line, @"\bName:\s*(.*?)\s*\|\s*Team ID:");
            var team = Regex.Match(line, @"\bTeam ID:\s*(\d+)\b"); var squad = Regex.Match(line, @"\bSquad ID:\s*(\d+|N/A)\b");
            var steam = Regex.Match(line, @"\b[Ss]team:\s*(\d{17})\b");
            if (id.Success && name.Success && team.Success && squad.Success) result.Add(new(id.Groups[1].Value, name.Groups[1].Value, team.Groups[1].Value, squad.Groups[1].Value, steam.Groups[1].Value, Regex.IsMatch(line, @"Is Leader:\s*(True|1)", RegexOptions.IgnoreCase)));
        }
        return result;
    }
}
