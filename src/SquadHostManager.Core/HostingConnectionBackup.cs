using System.Text.Json;
using System.Text.RegularExpressions;

namespace SquadHostManager.Core;

public sealed record SharedHostingProfile(string Endpoint,string? CertificateSha256,string AdminPassword,string SshFingerprint,string Label);

public static class HostingConnectionBackup
{
    public static SharedHostingProfile Parse(string json)
    {
        if(json.Length>16384)throw new ArgumentException("invalidInput");
        var profile=JsonSerializer.Deserialize<SharedHostingProfile>(json)??throw new ArgumentException("invalidInput");
        if(!Uri.TryCreate(profile.Endpoint,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.IsLoopback || uri.UserInfo!="" || uri.Query!="" || uri.Fragment!=""
            || profile.CertificateSha256!=null && !Regex.IsMatch(profile.CertificateSha256,@"^[A-Fa-f0-9]{64}$") || string.IsNullOrWhiteSpace(profile.AdminPassword))throw new ArgumentException("invalidInput");
        return profile;
    }
}
