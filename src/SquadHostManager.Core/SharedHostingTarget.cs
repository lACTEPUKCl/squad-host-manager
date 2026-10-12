using System.Net;
using System.Text.RegularExpressions;

namespace SquadHostManager.Core;
public record SharedHostingTarget(string Address,int SshPort,string User,int ServicePort)
{
    public string ServiceName=>$"squad-host-manager-app-{ServicePort}";
    public string Root=>$"/opt/{ServiceName}";
    public Uri Endpoint=>new UriBuilder("https",Address,ServicePort){Path="/"}.Uri;
    public void Validate()
    {
        if(SshPort is <1 or >65535 || ServicePort is <1024 or >65535 || SshPort==ServicePort)throw new ArgumentException("sharedInvalidPort");
        if(!Regex.IsMatch(User,@"^[a-z_][a-z0-9_-]{0,31}$"))throw new ArgumentException("sharedInvalidUser");
        if(Address.Length is <1 or >253 || Uri.CheckHostName(Address)==UriHostNameType.Unknown || Address.Contains('%') || Address=="localhost" || IPAddress.TryParse(Address,out var ip) && (IPAddress.IsLoopback(ip)||ip.Equals(IPAddress.Any)||ip.Equals(IPAddress.IPv6Any)))throw new ArgumentException("sharedInvalidAddress");
    }
}
