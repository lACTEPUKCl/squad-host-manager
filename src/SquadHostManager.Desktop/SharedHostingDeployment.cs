using System.IO;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Renci.SshNet;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public sealed class HostingSetupException(string key,Exception? cause=null) : Exception(key,cause)
{
    public string MessageKey { get; }=key;
}
public sealed class SshTrustRequired(string fingerprint):Exception("sharedTrustRequired")
{
    public string Fingerprint { get; }=fingerprint;
}
public sealed record HostingSshAccess(string? Password,string? KeyPath,string? KeyPassword,string? Fingerprint);
public sealed record VdsInspection(string Architecture,string System,long FreeMegabytes,bool ExistingInstallation,string? ProjectId=null,string? TeamId=null);
public sealed record SharedHostingProfile(string Endpoint,string CertificateSha256,string AdminPassword,string SshFingerprint,string Label);
public sealed record HostingSetupProgress(string Stage,double? UploadPercent=null);

public sealed partial class SharedHostingDeployment
{
    private static string Quote(string text)=>"'"+text.Replace("'","'\"'\"'")+"'";
    private static ConnectionInfo Connection(SharedHostingTarget target,HostingSshAccess access)
    {
        AuthenticationMethod method=access.KeyPath is {} path
            ?new PrivateKeyAuthenticationMethod(target.User,new PrivateKeyFile(path,access.KeyPassword))
            :new PasswordAuthenticationMethod(target.User,access.Password??"");
        return new(target.Address,target.SshPort,target.User,method){Timeout=TimeSpan.FromSeconds(20)};
    }
    private static void Pin(BaseClient client,HostingSshAccess access,Action<string> observed)
    {
        client.HostKeyReceived+=(_,key)=>
        {
            var fingerprint="SHA256:"+Convert.ToBase64String(SHA256.HashData(key.HostKey)).TrimEnd('=');observed(fingerprint);
            key.CanTrust=access.Fingerprint!=null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(fingerprint),Encoding.UTF8.GetBytes(access.Fingerprint));
        };
    }
    private static SshClient Open(SharedHostingTarget target,HostingSshAccess access)
    {
        var ssh=new SshClient(Connection(target,access)){KeepAliveInterval=TimeSpan.FromSeconds(15)};string? observed=null;Pin(ssh,access,value=>observed=value);
        try{ssh.Connect();return ssh;}
        catch
        {
            ssh.Dispose();
            if(observed!=null && access.Fingerprint==null)throw new SshTrustRequired(observed);
            if(observed!=null && observed!=access.Fingerprint)throw new HostingSetupException("sharedSshChanged");
            throw new HostingSetupException("sharedSshFailed");
        }
    }
    private static string Run(SshClient ssh,string command,TimeSpan? timeout=null)
    {
        using var job=ssh.CreateCommand(command);job.CommandTimeout=timeout??TimeSpan.FromSeconds(30);var output=job.Execute();
        if(job.ExitStatus!=0)throw new HostingSetupException("sharedRemoteFailed",new IOException(job.Error.Trim()));return output.Trim();
    }
    private static string Elevation(SshClient ssh)
    {
        if(Run(ssh,"id -u")=="0")return "";
        using var command=ssh.CreateCommand("sudo -n true");command.Execute();
        if(command.ExitStatus!=0)throw new HostingSetupException("sharedRootRequired");return "sudo -n ";
    }
    private static void RunInstall(SshClient ssh,string command,IProgress<HostingSetupProgress>? progress,CancellationToken ct)
    {
        using var job=ssh.CreateCommand(command);job.CommandTimeout=TimeSpan.FromMinutes(10);var executing=job.ExecuteAsync(ct);
        using var reader=new StreamReader(job.OutputStream);
        while(reader.ReadLineAsync(ct).AsTask().GetAwaiter().GetResult() is {} line)
            if(line.StartsWith("SHM_STAGE:",StringComparison.Ordinal) && line[10..] is "sharedDependencies" or "sharedFiles" or "sharedStartService" or "sharedFirewall")progress?.Report(new(line[10..]));
        executing.GetAwaiter().GetResult();
        if(job.ExitStatus!=0)throw new HostingSetupException(job.ExitStatus==41?"sharedDifferentAccount":job.ExitStatus==42?"sharedPortBusy":"sharedRemoteFailed",new IOException(job.Error.Trim()));
    }
    public Task<VdsInspection> Inspect(SharedHostingTarget target,HostingSshAccess access,CancellationToken ct=default)=>Task.Run(()=>
    {
        target.Validate();ct.ThrowIfCancellationRequested();using var ssh=Open(target,access);var elevate=Elevation(ssh);
        var arch=Run(ssh,"uname -m") switch{"x86_64"=>"linux-x64","aarch64"=>"linux-arm64",_=>throw new HostingSetupException("sharedUnsupportedSystem")};
        var os=Run(ssh,"cat /etc/os-release");
        var id=Regex.Match(os,"(?m)^ID=\"?([^\"\\r\\n]+)").Groups[1].Value;
        var version=Regex.Match(os,"(?m)^VERSION_ID=\"?([0-9.]+)").Groups[1].Value;
        if(!Version.TryParse(version.Contains('.')?version:version+".0",out var parsed)||!(id=="debian"&&parsed.Major>=12 || id=="ubuntu"&&parsed>=new Version(22,4)))throw new HostingSetupException("sharedUnsupportedSystem");
        Run(ssh,"command -v systemctl >/dev/null && command -v apt-get >/dev/null && command -v ss >/dev/null && command -v flock >/dev/null");
        var free=long.Parse(Run(ssh,"df -Pm /opt | awk 'NR==2 {print $4}'"),System.Globalization.CultureInfo.InvariantCulture);
        if(free<750)throw new HostingSetupException("sharedDiskFull");
        var memory=long.Parse(Run(ssh,"awk '/^MemTotal:/ {print $2}' /proc/meminfo"),System.Globalization.CultureInfo.InvariantCulture);
        if(memory<768*1024)throw new HostingSetupException("sharedMemoryLow");
        var existing=Run(ssh,elevate+"sh -c "+Quote($"if test -f {target.Root}/install.json; then printf yes; elif test -e {target.Root}; then printf foreign; else printf no; fi"));
        if(existing=="foreign")throw new HostingSetupException("sharedPortBusy");
        var listening=Run(ssh,$"ss -H -ltn 'sport = :{target.ServicePort}'");
        if(listening.Length>0 && existing!="yes")throw new HostingSetupException("sharedPortBusy");
        if(existing!="yes" && typeof(SharedHostingDeployment).Assembly.GetManifestResourceInfo("SquadHostManager.Coordinator."+arch)==null)throw new HostingSetupException("sharedPackageMissing");
        string? existingProject=null,existingTeam=null;
        if(existing=="yes")
        {
            using var marker=JsonDocument.Parse(Run(ssh,elevate+"cat "+Quote(target.Root+"/install.json")));
            if(marker.RootElement.GetProperty("application").GetString()!="squad-host-manager")throw new HostingSetupException("sharedPortBusy");
            existingProject=marker.RootElement.GetProperty("project").GetString();existingTeam=marker.RootElement.GetProperty("team").GetString();
        }
        return new VdsInspection(arch,id+" "+version,free,existing=="yes",existingProject,existingTeam);
    },ct);

    public async Task<SharedHostingProfile> Deploy(SharedHostingTarget target,HostingSshAccess access,CherryProject project,string token,IProgress<HostingSetupProgress>? progress=null,bool disableRental=false,CancellationToken ct=default)
    {
        var inspected=await Inspect(target,access,ct);
        if(!Regex.IsMatch(project.Id,@"^\d+$") || !Regex.IsMatch(project.TeamId,@"^\d+$"))throw new HostingSetupException("sharedDifferentAccount");
        var profile=await Task.Run(()=>Install(target,access,project,token,inspected,progress,disableRental,ct),ct);
        progress?.Report(new("sharedVerify"));
        using var client=HostingTransport.Client(new(profile.Endpoint),profile.CertificateSha256,TimeSpan.FromSeconds(5));
        for(var attempt=0;attempt<12;attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var health=await client.GetFromJsonAsync<JsonElement>("healthz",ct);
                if(health.GetProperty("application").GetString()!="squad-host-manager")throw new HostingSetupException("sharedVerifyFailed");
                using var login=await client.PostAsJsonAsync("api/login",new LoginRequest("admin",profile.AdminPassword),ct);login.EnsureSuccessStatusCode();
                var session=await login.Content.ReadFromJsonAsync<SessionResponse>(cancellationToken:ct);
                if(session?.IsAdmin!=true)throw new HostingSetupException("sharedVerifyFailed");
                client.DefaultRequestHeaders.Authorization=new("Bearer",session.Token);
                using var logout=await client.PostAsync("api/logout",null,ct);logout.EnsureSuccessStatusCode();
                return profile;
            }
            catch(Exception e) when(e is System.Net.Http.HttpRequestException or TaskCanceledException){if(attempt==11)throw new HostingSetupException("sharedPortBlocked");await Task.Delay(1000,ct);}
        }
        throw new HostingSetupException("sharedVerifyFailed");
    }
    private static SharedHostingProfile Install(SharedHostingTarget target,HostingSshAccess access,CherryProject project,string token,VdsInspection inspected,IProgress<HostingSetupProgress>? progress,bool disableRental,CancellationToken ct)
    {
        using var ssh=Open(target,access);var elevate=Elevation(ssh);string? temporary=null;
        var root=target.Root;var admin=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var certificatePassword=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var certificate=HostingTransport.CreateCertificate(target.Address,certificatePassword);string? oldConfig=null;
        var existingConfig=inspected.ExistingInstallation && Run(ssh,elevate+"sh -c "+Quote($"test -f {root}/app/appsettings.Production.json && printf yes || printf no"))=="yes";
        if(inspected.ExistingInstallation)
        {
            using var marker=JsonDocument.Parse(Run(ssh,elevate+"cat "+Quote(root+"/install.json")));
            if(marker.RootElement.GetProperty("project").GetString()!=project.Id || marker.RootElement.GetProperty("team").GetString()!=project.TeamId)throw new HostingSetupException("sharedDifferentAccount");
            if(!existingConfig && Run(ssh,elevate+"sh -c "+Quote($"test -e {root}/data/pool.db && printf yes || printf no"))=="yes")throw new HostingSetupException("sharedConfigurationMissing");
        }
        if(existingConfig)
        {
            oldConfig=Run(ssh,elevate+"cat "+Quote(root+"/app/appsettings.Production.json"));
            using var existing=JsonDocument.Parse(oldConfig);
            if(existing.RootElement.GetProperty("Cherry").GetProperty("ProjectId").GetString()!=project.Id || existing.RootElement.GetProperty("Cherry").GetProperty("TeamId").GetString()!=project.TeamId)throw new HostingSetupException("sharedDifferentAccount");
            admin=existing.RootElement.GetProperty("Admin").GetProperty("Password").GetString()!;
            certificatePassword=existing.RootElement.GetProperty("Kestrel").GetProperty("Endpoints").GetProperty("Https").GetProperty("Certificate").GetProperty("Password").GetString()!;
            certificate=Convert.FromBase64String(Run(ssh,elevate+"base64 -w0 "+Quote(root+"/private/tls.pfx")));
        }
        using var cert=X509CertificateLoader.LoadPkcs12(certificate,certificatePassword,X509KeyStorageFlags.EphemeralKeySet);
        var fingerprint=Convert.ToHexString(SHA256.HashData(cert.RawData));
        var settings=JsonSerializer.Serialize(new{DataDir=root+"/data",Admin=new{Password=admin},Cherry=new{TokenFile=root+"/private/cherry-token",TeamId=project.TeamId,ProjectId=project.Id,Region="DE-Frankfurt",Plan="amd-ryzen-9950x",DisableRent=disableRental,DisableRelease=disableRental},Ssh=new{KeyPath=root+"/private/cherry-ssh",User="root"},Hosting=new{PersonalMode=false},Kestrel=new{Endpoints=new{Https=new{Url=$"https://*:{target.ServicePort}",Certificate=new{Path=root+"/private/tls.pfx",Password=certificatePassword}}}},Logging=new{LogLevel=new Dictionary<string,string>{{"Microsoft.AspNetCore","Warning"}}}});
        try
        {
            temporary=Run(ssh,"umask 077; mktemp -d /tmp/shm-setup.XXXXXXXX");
            if(!Regex.IsMatch(temporary,@"^/tmp/shm-setup\.[A-Za-z0-9]{8}$"))throw new HostingSetupException("sharedRemoteFailed");
            using var sftp=new SftpClient(Connection(target,access));Pin(sftp,access,_=>{});sftp.Connect();
            void Upload(string name,byte[] data){using var stream=new MemoryStream(data);sftp.UploadFile(stream,temporary+"/"+name,true);}
            if(existingConfig && Run(ssh,elevate+"systemctl show -p LoadState --value "+Quote(target.ServiceName+".service"))=="loaded")
            {
                Upload("token",Encoding.UTF8.GetBytes(token.Trim()));
                Run(ssh,elevate+"sh -c "+Quote($"exec 9>/run/lock/{target.ServiceName}.lock; flock -w 120 9; test ! -e {root}/removal.pending && install -m 640 -o root -g shm-app-{target.ServicePort} {temporary}/token {root}/private/token.next && mv -f {root}/private/token.next {root}/private/cherry-token && systemctl enable {target.ServiceName}.service >/dev/null && systemctl start {target.ServiceName}.service"));
                return new(target.Endpoint.ToString(),fingerprint,admin,access.Fingerprint!,target.Address+":"+target.ServicePort);
            }
            progress?.Report(new("sharedUpload"));
            using(var payload=typeof(SharedHostingDeployment).Assembly.GetManifestResourceStream("SquadHostManager.Coordinator."+inspected.Architecture)!)
            {
                var length=payload.Length;var last=-1d;sftp.UploadFile(payload,temporary+"/service",true,bytes=>{ct.ThrowIfCancellationRequested();var percent=bytes*100d/length;if(percent-last>=1 || bytes==(ulong)length){last=percent;progress?.Report(new("sharedUpload",percent));}});
            }
            Upload("settings.json",Encoding.UTF8.GetBytes(settings));Upload("token",Encoding.UTF8.GetBytes(token.Trim()));Upload("tls.pfx",certificate);
            if(!existingConfig){using var key=RSA.Create(3072);Upload("ssh-key",Encoding.UTF8.GetBytes(key.ExportRSAPrivateKeyPem()));}
            Upload("install.json",JsonSerializer.SerializeToUtf8Bytes(new{application="squad-host-manager",project=project.Id,team=project.TeamId,certificateSha256=fingerprint}));
            Upload("unit",Encoding.UTF8.GetBytes(Unit(target)));Upload("install.sh",Encoding.UTF8.GetBytes(Script(target,temporary)));
            progress?.Report(new("sharedInstall"));ct.ThrowIfCancellationRequested();RunInstall(ssh,elevate+"bash "+Quote(temporary+"/install.sh"),progress,ct);
            using var installed=JsonDocument.Parse(Run(ssh,elevate+"cat "+Quote(root+"/app/appsettings.Production.json")));
            admin=installed.RootElement.GetProperty("Admin").GetProperty("Password").GetString()!;
            certificatePassword=installed.RootElement.GetProperty("Kestrel").GetProperty("Endpoints").GetProperty("Https").GetProperty("Certificate").GetProperty("Password").GetString()!;
            using var installedCert=X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(Run(ssh,elevate+"base64 -w0 "+Quote(root+"/private/tls.pfx"))),certificatePassword,X509KeyStorageFlags.EphemeralKeySet);
            fingerprint=Convert.ToHexString(SHA256.HashData(installedCert.RawData));
            return new(target.Endpoint.ToString(),fingerprint,admin,access.Fingerprint!,target.Address+":"+target.ServicePort);
        }
        finally
        {
            if(temporary!=null && Regex.IsMatch(temporary,@"^/tmp/shm-setup\.[A-Za-z0-9]{8}$"))
            {try{Run(ssh,elevate+"rm -rf -- "+Quote(temporary));}catch{}}
        }
    }
    public static string Unit(SharedHostingTarget target)
    {
        target.Validate();var root=target.Root;var user="shm-app-"+target.ServicePort;
        return $"""
        [Unit]
        Description=Squad Host Manager shared hosting
        After=network-online.target
        Wants=network-online.target
        [Service]
        User={user}
        Group={user}
        WorkingDirectory={root}/app
        ExecStart={root}/app/service
        Environment=ASPNETCORE_ENVIRONMENT=Production
        Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
        Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR={root}/runtime
        Restart=on-failure
        RestartSec=5
        NoNewPrivileges=true
        PrivateTmp=true
        ProtectSystem=strict
        ProtectHome=true
        ReadWritePaths={root}/data {root}/runtime
        RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
        UMask=0077
        MemoryMax=512M
        [Install]
        WantedBy=multi-user.target
        """.Replace("\r\n","\n")+"\n";
    }
    public static string Script(SharedHostingTarget target,string staging)
    {
        target.Validate();if(!Regex.IsMatch(staging,@"^/tmp/shm-setup\.[A-Za-z0-9]{8}$"))throw new ArgumentException("Invalid staging path.");
        var root=target.Root;var user="shm-app-"+target.ServicePort;
        return $$"""
        set -euo pipefail
        root='{{root}}'
        staging='{{staging}}'
        user='{{user}}'
        service='{{target.ServiceName}}'
        port='{{target.ServicePort}}'
        test ! -L "$root"
        exec 9>"/run/lock/$service.lock"
        flock -w 120 9
        test ! -e "$root/removal.pending" || exit 42
        if test -e "$root" && ! test -f "$root/install.json"; then exit 42; fi
        project=$(sed -n 's/.*"project":"\([0-9]*\)".*/\1/p' "$staging/install.json")
        team=$(sed -n 's/.*"team":"\([0-9]*\)".*/\1/p' "$staging/install.json")
        if test -f "$root/install.json"; then
          grep -F '"project":"'"$project"'"' "$root/install.json" >/dev/null || exit 41
          grep -F '"team":"'"$team"'"' "$root/install.json" >/dev/null || exit 41
          if test -f "$root/app/appsettings.Production.json" && test "$(systemctl show -p LoadState --value "$service.service")" = loaded; then
            install -m 640 -o root -g "$user" "$staging/token" "$root/private/token.next"
            mv -f "$root/private/token.next" "$root/private/cherry-token"
            systemctl enable "$service.service" >/dev/null
            systemctl start "$service.service"
            exit 0
          fi
        fi
        printf 'SHM_STAGE:sharedDependencies\n'
        if ! ldconfig -p | grep 'libssl.so.3' >/dev/null || ! ldconfig -p | grep 'libstdc++.so.6' >/dev/null || ! ldconfig -p | grep 'libz.so.1' >/dev/null; then
          apt-get update -qq
          ssl=libssl3
          if apt-cache show libssl3t64 >/dev/null 2>&1; then ssl=libssl3t64; fi
          DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ca-certificates libstdc++6 zlib1g "$ssl"
        fi
        if ! id "$user" >/dev/null 2>&1; then useradd --system --no-create-home --shell /usr/sbin/nologin "$user"; fi
        printf 'SHM_STAGE:sharedFiles\n'
        install -d -m 755 "$root" "$root/app"
        install -m 600 "$staging/install.json" "$root/install.json"
        install -d -m 700 -o "$user" -g "$user" "$root/data" "$root/runtime"
        install -d -m 750 -o root -g "$user" "$root/private"
        if test -e "$root/app/service"; then cp -p "$root/app/service" "$root/app/service.previous"; fi
        if test -e "$root/app/appsettings.Production.json"; then cp -p "$root/app/appsettings.Production.json" "$root/app/settings.previous"; fi
        install -m 755 "$staging/service" "$root/app/service.next"
        install -m 640 -o root -g "$user" "$staging/settings.json" "$root/app/settings.next"
        install -m 640 -o root -g "$user" "$staging/token" "$root/private/cherry-token"
        install -m 640 -o root -g "$user" "$staging/tls.pfx" "$root/private/tls.pfx"
        if test ! -e "$root/private/cherry-ssh"; then install -m 600 -o "$user" -g "$user" "$staging/ssh-key" "$root/private/cherry-ssh"; fi
        mv -f "$root/app/service.next" "$root/app/service"
        mv -f "$root/app/settings.next" "$root/app/appsettings.Production.json"
        install -m 600 "$staging/install.json" "$root/install.json"
        install -m 644 "$staging/unit" "/etc/systemd/system/$service.service"
        printf 'SHM_STAGE:sharedStartService\n'
        systemctl daemon-reload
        systemctl enable "$service.service" >/dev/null
        systemctl restart "$service.service"
        printf 'SHM_STAGE:sharedFirewall\n'
        if command -v ufw >/dev/null && ufw status | grep 'Status: active' >/dev/null; then ufw allow "$port/tcp" >/dev/null; fi
        if command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then firewall-cmd --permanent --add-port="$port/tcp" >/dev/null; firewall-cmd --reload >/dev/null; fi
        for attempt in $(seq 1 30); do
          if systemctl is-active --quiet "$service.service"; then exit 0; fi
          sleep .5
        done
        if test -e "$root/app/service.previous"; then
          cp -p "$root/app/service.previous" "$root/app/service"
          cp -p "$root/app/settings.previous" "$root/app/appsettings.Production.json"
          systemctl restart "$service.service"
        fi
        exit 1
        """.Replace("\r\n","\n")+"\n";
    }
}
