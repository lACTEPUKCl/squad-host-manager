using System.Text.Json;

namespace SquadHostManager.Core;
public record ConnectionInvite(string Endpoint,string Key,string? CertificateSha256=null)
{
    public string Encode()=>"SHM1."+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this)).TrimEnd('=').Replace('+','-').Replace('/','_');
    public static ConnectionInvite? Decode(string value)
    {
        if(!value.StartsWith("SHM1.",StringComparison.Ordinal))return null;
        if(value.Length>4096)throw new ArgumentException("Invalid invitation.");
        try
        {
            var data=value[5..].Replace('-','+').Replace('_','/');data=data.PadRight((data.Length+3)/4*4,'=');
            var invite=JsonSerializer.Deserialize<ConnectionInvite>(Convert.FromBase64String(data))!;
            if(!Uri.TryCreate(invite.Endpoint,UriKind.Absolute,out var uri)||uri.UserInfo!=""||uri.Fragment!=""||uri.Query!=""||uri.Scheme!="https" && !(uri.Scheme=="http"&&uri.IsLoopback)||string.IsNullOrWhiteSpace(invite.Key)||invite.Key.Length>512)throw new ArgumentException("Invalid invitation.");
            if(invite.CertificateSha256!=null && (uri.Scheme!="https" || invite.CertificateSha256.Length!=64 || invite.CertificateSha256.Any(c=>!Uri.IsHexDigit(c))))throw new ArgumentException("Invalid certificate fingerprint.");
            return invite;
        }
        catch(Exception e) when(e is FormatException or JsonException or NullReferenceException){throw new ArgumentException("Invalid invitation.");}
    }
}
