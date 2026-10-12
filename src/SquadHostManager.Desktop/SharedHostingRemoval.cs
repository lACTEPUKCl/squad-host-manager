using SquadHostManager.Core;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Renci.SshNet;

namespace SquadHostManager.Desktop;

public sealed partial class SharedHostingDeployment
{
    public async Task Remove(SquadHostManager.Core.SharedHostingTarget target,HostingSshAccess access,SharedHostingProfile profile,string backupPath,CancellationToken ct=default)
    {
        var inspected=await Inspect(target,access,ct);
        if(!inspected.ExistingInstallation || target.Endpoint.ToString()!=profile.Endpoint)throw new HostingSetupException("sharedRemovalMismatch");
        await Task.Run(()=>
        {
            using var ssh=Open(target,access);var elevate=Elevation(ssh);var root=target.Root;var service=target.ServiceName;var temporary=Run(ssh,"umask 077; mktemp -d /tmp/shm-setup.XXXXXXXX");
            if(!System.Text.RegularExpressions.Regex.IsMatch(temporary,@"^/tmp/shm-setup\.[A-Za-z0-9]{8}$"))throw new HostingSetupException("sharedRemoteFailed");
            var reserved=false;var removed=false;var wasActive=Run(ssh,"systemctl is-active "+Quote(service+".service")+" || true")=="active";
            try
            {
                using var settings=JsonDocument.Parse(Run(ssh,elevate+"cat "+Quote(root+"/app/appsettings.Production.json")));
                if(settings.RootElement.GetProperty("Admin").GetProperty("Password").GetString()!=profile.AdminPassword)throw new HostingSetupException("sharedRemovalMismatch");
                var password=settings.RootElement.GetProperty("Kestrel").GetProperty("Endpoints").GetProperty("Https").GetProperty("Certificate").GetProperty("Password").GetString()!;
                using var cert=X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(Run(ssh,elevate+"base64 -w0 "+Quote(root+"/private/tls.pfx"))),password,X509KeyStorageFlags.EphemeralKeySet);
                if(Convert.ToHexString(SHA256.HashData(cert.RawData))!=profile.CertificateSha256)throw new HostingSetupException("sharedRemovalMismatch");
                using var sftp=new SftpClient(Connection(target,access));Pin(sftp,access,_=>{});sftp.Connect();
                using(var payload=typeof(SharedHostingDeployment).Assembly.GetManifestResourceStream("SquadHostManager.Coordinator."+inspected.Architecture)!)sftp.UploadFile(payload,temporary+"/checker",true);
                var reserve=$"set -eu; exec 9>/run/lock/{service}.lock; flock -w 120 9; test ! -L {root}; test ! -e {root}/removal.pending; touch {root}/removal.pending; systemctl stop {service}.service || {{ rm -f {root}/removal.pending; exit 1; }}";
                Run(ssh,elevate+"sh -c "+Quote(reserve),TimeSpan.FromMinutes(2));reserved=true;
                using(var check=ssh.CreateCommand(elevate+"sh -c "+Quote($"chmod 755 {temporary}/checker; DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_BUNDLE_EXTRACT_BASE_DIR={temporary}/runtime {temporary}/checker --check-removal {root}/data")))
                {
                    check.CommandTimeout=TimeSpan.FromMinutes(1);check.Execute();
                    if(check.ExitStatus!=0)throw new HostingSetupException(check.ExitStatus==43?"sharedRemovalBusy":"sharedRemovalCheckFailed");
                }
                ct.ThrowIfCancellationRequested();
                var uid=Run(ssh,"id -u");var gid=Run(ssh,"id -g");
                Run(ssh,elevate+"sh -c "+Quote($"tar -czf {temporary}/backup.tar.gz -C {root} data install.json; chown {uid}:{gid} {temporary}/backup.tar.gz; chmod 600 {temporary}/backup.tar.gz"));
                using(var file=new FileStream(backupPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))sftp.DownloadFile(temporary+"/backup.tar.gz",file);
                ct.ThrowIfCancellationRequested();
                var script=$"set -eu; exec 9>/run/lock/{service}.lock; flock -w 120 9; test -f {root}/removal.pending; test ! -L {root}; test \"$(readlink -f {root})\" = {root}; systemctl disable --now {service}.service; rm -f /etc/systemd/system/{service}.service; systemctl daemon-reload; rm -rf -- {root}; if command -v ufw >/dev/null && ufw status | grep 'Status: active' >/dev/null; then ufw delete allow {target.ServicePort}/tcp >/dev/null || true; fi; if command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then firewall-cmd --permanent --remove-port={target.ServicePort}/tcp >/dev/null || true; firewall-cmd --reload >/dev/null || true; fi; userdel shm-app-{target.ServicePort} 2>/dev/null || true";
                Run(ssh,elevate+"sh -c "+Quote(script),TimeSpan.FromMinutes(2));removed=true;
            }
            finally
            {
                if(reserved && !removed)try{Run(ssh,elevate+"sh -c "+Quote($"rm -f {root}/removal.pending; "+(wasActive?$"systemctl start {service}.service":"true")));}catch{}
                try{Run(ssh,elevate+"rm -rf -- "+Quote(temporary));}catch{}
            }
        },ct);
    }
}
