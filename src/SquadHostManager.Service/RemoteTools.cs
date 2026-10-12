using System.Globalization;
using System.Text.Json;
using Renci.SshNet;
using SquadHostManager.Core;

namespace SquadHostManager.Service;

public sealed partial class RemoteDocker
{
    public Task<LogBatch> MatchLogs(string hostId,string ip,Instance instance,CancellationToken ct)=>With(hostId,ip,ssh=>JsonSerializer.Deserialize<LogBatch>(Run(ssh,ScopedFiles.Command(instance.Id,"matchlogs",content:JsonSerializer.Serialize(instance.LogPositions)),TimeSpan.FromSeconds(30)))??throw new IOException("Invalid match logs"),ct);
    public async Task<MetricSample> Metrics(string hostId,string ip,Instance instance,CancellationToken ct)
    {
        var result=await With(hostId,ip,ssh=>
        {
            var stats=Run(ssh,"docker stats --no-stream --format '{{json .}}' "+Container(instance.Id),TimeSpan.FromSeconds(15)).Trim();
            var free=Run(ssh,"df -B1 --output=avail /opt/squad-host-manager | tail -n 1").Trim();
            var size=Run(ssh,ScopedFiles.Command(instance.Id,"size"),TimeSpan.FromSeconds(15)).Trim();
            double? cpu=null;long? memory=null,limit=null;
            if(stats.Length>0){using var doc=JsonDocument.Parse(stats);var value=doc.RootElement;cpu=double.TryParse(value.GetProperty("CPUPerc").GetString()?.TrimEnd('%'),NumberStyles.Float,CultureInfo.InvariantCulture,out var percent)?percent:null;var parts=value.GetProperty("MemUsage").GetString()!.Split('/');memory=ParseBytes(parts[0]);limit=parts.Length>1?ParseBytes(parts[1]):null;}
            return new MetricSample(DateTimeOffset.UtcNow,cpu,memory,limit,long.TryParse(size,out var bytes)?bytes:null,long.TryParse(free,out var available)?available:null,null,null);
        },ct);
        if(instance.GameState=="gameReady")
        {
            try{var players=SquadPlayers.Parse(await Command(hostId,ip,instance,"ListPlayers",ct)).Count;var map=await Command(hostId,ip,instance,"ShowCurrentMap",ct);result=result with{Players=players,Layer=map.Length<=300?map.Trim():null};}
            catch(Exception e) when(e is not OperationCanceledException){result=result with{Error="rconUnavailable"};}
        }
        return result;
    }
    public static long? ParseBytes(string text)
    {
        var m=System.Text.RegularExpressions.Regex.Match(text.Trim(),@"^([0-9.]+)\s*(B|KiB|MiB|GiB|TiB|kB|MB|GB)$");
        if(!m.Success || !double.TryParse(m.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var value))return null;
        var factor=m.Groups[2].Value switch{"KiB"=>1024d,"MiB"=>1048576d,"GiB"=>1073741824d,"TiB"=>1099511627776d,"kB"=>1000d,"MB"=>1000000d,"GB"=>1000000000d,_=>1d};return (long)(value*factor);
    }
    public Task RestoreFiles(string hostId,string ip,string instanceId,string localArchive,CancellationToken ct)=>With(hostId,ip,ssh=>
    {
        var remote="/tmp/shm-restore-"+Guid.NewGuid().ToString("N");
        try
        {
            using var key=new PrivateKeyFile(config["Ssh:KeyPath"]!);var user=config["Ssh:User"]??"root";
            using var sftp=new SftpClient(new Renci.SshNet.ConnectionInfo(ip,config.GetValue<int?>("Ssh:Port")??22,user,new PrivateKeyAuthenticationMethod(user,key)));
            var pin=store.Load<string>("ssh-pin-"+hostId)??throw new IOException("SSH fingerprint missing.");
            sftp.HostKeyReceived+=(_,e)=>e.CanTrust=Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(e.HostKey))==pin;sftp.Connect();
            using(var source=File.OpenRead(localArchive))sftp.UploadFile(source,remote,false);sftp.ChangePermissions(remote,384);ct.ThrowIfCancellationRequested();
            return Run(ssh,ScopedFiles.Command(instanceId,"restore",remote),TimeSpan.FromMinutes(10));
        }
        finally{Run(ssh,"rm -f -- "+Quote(remote));}
    },ct);
}
