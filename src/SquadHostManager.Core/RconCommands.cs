using System.Text.RegularExpressions;

namespace SquadHostManager.Core;

public record RconParameter(string Name,bool Optional);
public static class RconCommands
{
    private const string ParameterPattern=@"(?<optional>\[\s*)?(?<quoted>"")?<(?<name>[^<>]+)>(?(quoted)"")(?(optional)\s*\])";
    public static IReadOnlyList<RconParameter> Parameters(string usage)=>Regex.Matches(usage,ParameterPattern).Select(m=>new RconParameter(m.Groups["name"].Value,m.Groups["optional"].Success)).ToArray();
    public static bool ValidBanDuration(string value)=>Regex.IsMatch(value,@"^(0|[1-9]\d{0,5}[mhdwMy]?)$");
    public static string PlayerAction(string action,string steamId,string reason="",string duration="")
    {
        if(!Regex.IsMatch(steamId,@"^\d{17}$"))throw new ArgumentException("SteamID is required.",nameof(steamId));
        if(action is not ("AdminKick" or "AdminBan" or "AdminWarn" or "AdminForceTeamChange"))throw new ArgumentException("Unsupported player action.",nameof(action));
        var target=action+" \""+steamId+"\"";
        if(action=="AdminForceTeamChange")return target;
        if(action=="AdminBan" && !ValidBanDuration(duration))throw new ArgumentException("Invalid ban duration.",nameof(duration));
        if(string.IsNullOrWhiteSpace(reason) || reason.Any(char.IsControl))throw new ArgumentException("A single-line reason is required.",nameof(reason));
        return Validate(target+(action=="AdminBan"?" "+duration:"")+" "+reason.Trim());
    }
    public static string Validate(string command)
    {
        if(string.IsNullOrWhiteSpace(command) || command.Length>1024 || command.Any(char.IsControl))throw new ArgumentException("Invalid RCON command.");
        return command;
    }
    public static bool Rejected(string reply)=>Regex.IsMatch(reply.Trim(),@"^(?:Admin\w+:\s*)?(?:Error\b|Failed\b|Unable\b|Could not\b|Cannot\b|Unknown command\b|Unrecognized command\b|Invalid\b|No (?:such )?player\b|Player (?:not found|does not exist)\b|Permission denied\b|Access denied\b|You (?:do not|don't) have permission\b)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    public static string FromUsage(string name,string usage,IReadOnlyList<string> values)
    {
        if(!usage.StartsWith(name,StringComparison.Ordinal) || usage.Length>name.Length && !char.IsWhiteSpace(usage[name.Length]))throw new ArgumentException("Invalid command template.");
        var parameters=Regex.Matches(usage,ParameterPattern);
        if(parameters.Count!=values.Count)throw new ArgumentException("Invalid parameter count.");
        var index=0;var omitted=false;
        var result=Regex.Replace(usage,ParameterPattern,match=>
        {
            var value=values[index++].Trim();var parameter=match.Groups["name"].Value;
            if(value.Length==0 && match.Groups["optional"].Success){omitted=true;return "";}
            if(omitted)throw new ArgumentException("Optional parameters must be filled in order.");
            if(value.Length==0 || value.Any(char.IsControl))throw new ArgumentException("Invalid command parameter.");
            if(parameter=="PlayerId" && (!int.TryParse(value,out var playerId) || playerId<0 || !Regex.IsMatch(value,@"^\d+$")))throw new ArgumentException("Invalid player ID.");
            if(parameter=="BanLength" && !ValidBanDuration(value))throw new ArgumentException("Invalid ban duration.");
            var quoted=match.Groups["quoted"].Success || parameter.Contains("NameOr",StringComparison.Ordinal);
            if(quoted && value.Contains('"'))throw new ArgumentException("Quotes are not allowed inside a quoted parameter.");
            return quoted?"\""+value+"\"":value;
        });
        return Validate(result.Trim());
    }
}
