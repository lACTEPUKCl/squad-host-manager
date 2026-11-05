using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using SquadHostManager.Core;
namespace SquadHostManager.Service;

public record RespawnAllocation(int PlanId,int MemoryMb,decimal HourlyPrice,string Region);
public record RespawnManagedServer(string Uuid,string OwnerId,PortSet ReservedPorts,RespawnAllocation Plan);
public sealed class RespawnInfrastructure(RespawnHost api,Store store,Func<RespawnConnection,string,CancellationToken,Task<string>>? execute=null):IInfrastructure,IDisposable
{
    public const string Prefix="respawn:";
    private readonly ConcurrentDictionary<string,(RespawnConnection Info,RconConnection Rcon)> connections=[];
    private readonly ConcurrentDictionary<string,SemaphoreSlim> gates=[];
    private RespawnManagedServer Managed(string id)=>store.Load<RespawnManagedServer>("respawn-instance-"+id)??throw new InvalidOperationException("Сервер RespawnHost ещё не создан или результат заказа не подтверждён.");
    public void Dispose(){foreach(var c in connections.Values)c.Rcon.Dispose();connections.Clear();}
    private async Task ForgetSafe(string id,CancellationToken ct){var gate=gates.GetOrAdd(id,_=>new(1));await gate.WaitAsync(ct);try{Forget(id);}finally{gate.Release();}}
    private void Forget(string id){if(connections.TryRemove(id,out var session))session.Rcon.Dispose();}
    private async Task WaitStopped(string uuid,CancellationToken ct)
    {
        var deadline=DateTimeOffset.UtcNow.AddMinutes(3);
        while(true){var server=(await api.Servers(ct)).Find(s=>s.Id==uuid);if(server?.State is "offline" or "stopped")return;if(DateTimeOffset.UtcNow>deadline)throw new TimeoutException("RespawnHost не подтвердил остановку.");await Task.Delay(2000,ct);}
    }
    public async Task Start(string host,string ip,Instance instance,CancellationToken ct)
    {
        await ForgetSafe(instance.Id,ct);
        var managed=store.Load<RespawnManagedServer>("respawn-instance-"+instance.Id);
        if(managed==null)
        {
            var plan=store.Load<RespawnAllocation>("respawn-plan-"+instance.OwnerId)??throw new InvalidOperationException("Выберите тариф RespawnHost перед созданием сервера.");
            var order=Guid.Parse(instance.Id);var response=store.Load<JsonElement?>("respawn-order-result-"+order);
            var orderState=store.Load<string>("respawn-order-"+order);
            if(response==null && orderState is null or "rejected")managed=await api.Reusable(instance.OwnerId,plan,ct);
            if(managed!=null)store.Save("respawn-instance-"+instance.Id,managed with{ReservedPorts=instance.Ports});
            else
            {
            response??=await api.Rent(new(order,plan.PlanId,plan.HourlyPrice,plan.Region),ct);
            var body=response.Value.TryGetProperty("data",out var data)?data:response.Value;
            var uuid=body.GetProperty("panelUuid").GetString()!;if(!Guid.TryParse(uuid,out _))throw new IOException("Нет UUID арендованного сервера.");
            managed=new(uuid,instance.OwnerId,instance.Ports,plan);store.Save("respawn-instance-"+instance.Id,managed);
            }
        }
        if(managed.OwnerId!=instance.OwnerId)throw new UnauthorizedAccessException("Сервер принадлежит другому пользователю.");
        var deadline=DateTimeOffset.UtcNow.AddMinutes(15);
        while(!(await api.Servers(ct)).Any(s=>s.Id==managed.Uuid && s.State is "running" or "offline" or "stopped"))
        {if(DateTimeOffset.UtcNow>deadline)throw new TimeoutException("RespawnHost ещё устанавливает сервер.");await Task.Delay(3000,ct);}
        await api.WaitStartup(managed.Uuid,ct);
        if((await api.Servers(ct)).Any(s=>s.Id==managed.Uuid && s.State=="running")){RespawnMetering.Begin(store,instance,managed.Plan.HourlyPrice);await api.Power(managed.Uuid,"stop",ct);await WaitStopped(managed.Uuid,ct);RespawnMetering.End(store,instance.Id);}
        var desiredMods=string.Join(' ',System.Text.RegularExpressions.Regex.Matches(instance.Mods,@"\d+").Select(m=>m.Value).Distinct());
        var installedMods=store.Load<string>("respawn-installed-mods-"+managed.Uuid)??"";
        if(desiredMods!=installedMods)await Update(host,ip,instance,ct);
        var configs=instance.CommonConfigs??StandardConfigs.Copy();
        foreach(var config in configs.Where(p=>RemoteDocker.ConfigFiles.Contains(p.Key)))
        {
            if(config.Key=="License.cfg" && string.IsNullOrWhiteSpace(config.Value))continue;
            var content=config.Key=="Server.cfg"?SquadConfig.Identity(instance.ServerConfig??config.Value,instance.Name,instance.Password,instance.MaxPlayers):config.Value;
            await Config(host,ip,instance.Id,new(config.Key,content),ct);
        }
        if(!string.IsNullOrWhiteSpace(instance.StartLayer))await Config(host,ip,instance.Id,new("LayerRotation.cfg",instance.StartLayer+"\n"),ct);
        await api.ConfigureRcon(managed.Uuid,instance.RconPassword,ct);
        await api.ApplyWorkshop(managed.Uuid,instance.Mods,ct);
        await api.ApplyIdentity(managed.Uuid,instance,ct);
        await api.Alias(managed.Uuid,instance.Name,ct);
        RespawnMetering.Begin(store,instance,managed.Plan.HourlyPrice);
        await api.Power(managed.Uuid,"start",ct);
        instance.Address=(await api.ConnectionInfo(managed.Uuid,ct)).GetProperty("connectionString").GetString();
    }
    public async Task Update(string host,string ip,Instance instance,CancellationToken ct)
    {
        var uuid=Managed(instance.Id).Uuid;
        await api.WaitStartup(uuid,ct);
        var path=Path.Combine(store.DirectoryPath,"respawn-update-"+instance.Id+".tar.gz");
        // Keep local recovery data before asking the provider to update/reinstall in place.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary=path+".part-"+Guid.NewGuid().ToString("N");
        var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write};if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        try
        {
            await using(var output=new FileStream(temporary,options))await using(var archive=await ArchiveStream(host,ip,instance.Id,ct)){await BackupFormat.Copy(archive,output,2L*1024*1024*1024,ct);await output.FlushAsync(ct);output.Flush(true);}
            File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
        var license=await Config(host,ip,instance.Id,new("License.cfg"),ct);
        store.Save("respawn-update-license-"+instance.Id,license);
        await api.ApplyWorkshop(uuid,instance.Mods,ct);
        // Subscribe before the request: installation does not change the node's power state.
        await using var monitor=await api.WatchInstall(uuid,ct);
        await api.ReinstallKeepingFiles(uuid,ct);
        using var installTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);installTimeout.CancelAfter(TimeSpan.FromMinutes(20));
        await monitor.Wait(installTimeout.Token);
        _=await api.ReadFile(uuid,"/SquadGame/ServerConfig/Server.cfg",ct);
        await RestoreFiles(host,ip,instance.Id,path,ct);
        if(!string.IsNullOrWhiteSpace(license))await Config(host,ip,instance.Id,new("License.cfg",license),ct);
        store.Save("respawn-installed-mods-"+uuid,string.Join(' ',System.Text.RegularExpressions.Regex.Matches(instance.Mods,@"\d+").Select(m=>m.Value).Distinct()));
        await ForgetSafe(instance.Id,ct);
    }
    public async Task Stop(string host,string ip,string id,CancellationToken ct)
    {
        var managed=Managed(id);await ForgetSafe(id,ct);
        await api.Power(managed.Uuid,"stop",ct);await WaitStopped(managed.Uuid,ct);RespawnMetering.End(store,id);
    }
    public Task<RemoteHost?> FindHost(string marker,CancellationToken ct)=>Task.FromResult<RemoteHost?>(null);
    public Task<RemoteHost> Rent(string marker,CancellationToken ct)=>Task.FromResult(new RemoteHost(Prefix+marker,"respawnhost.com",true));
    public async Task<RemoteHost?> GetHost(string id,CancellationToken ct)
    {
        if(store.Load<bool>("respawn-closed-"+id))return null;
        _=await api.Servers(ct);return new(id,"respawnhost.com",true);
    }
    public async Task Prepare(string host,string ip,CancellationToken ct){_=await api.Servers(ct);}
    public async Task Delete(string host,CancellationToken ct)
    {
        if(await api.HasServers(ct))throw new InvalidOperationException("Сначала остановите все серверы RespawnHost.");
        // PAYG instances remain allocated but offline; no files or remote servers are deleted.
        store.Save("respawn-closed-"+host,true);
    }
    public async Task<List<RemoteInstance>> Inventory(string host,string ip,CancellationToken ct)
    {
        var primary=store.Load<PoolState>("pool");
        var state=primary?.Provider=="respawn"?primary:store.Load<PoolState>("pool-respawn")??new();var maps=state.Instances.Select(i=>(i.Id,Map:store.Load<RespawnManagedServer>("respawn-instance-"+i.Id))).Where(x=>x.Map!=null).ToList();
        return (await api.Servers(ct)).Select(s=>
        {
            var own=maps.Find(m=>m.Map!.Uuid==s.Id);
            if(own.Map!=null && s.State is "offline" or "stopped")RespawnMetering.End(store,own.Id);
            if(own.Map!=null && s.State=="running" && state.Instances.Find(i=>i.Id==own.Id) is {} instance)RespawnMetering.Begin(store,instance,own.Map.Plan.HourlyPrice);
            var pending=store.Load<Dictionary<string,string>>("respawn-power-pending")??[];
            var starting=pending.TryGetValue(s.Id,out var target)&&target!="stop";
            return new RemoteInstance(own.Map==null?"external:"+s.Id:own.Id,starting || s.State is not ("offline" or "stopped"),own.Map?.ReservedPorts.All??[]);
        }).ToList();
    }
    public Task<HashSet<int>> BoundPorts(string host,string ip,CancellationToken ct)=>Task.FromResult(new HashSet<int>());
    public async Task<string> Config(string host,string ip,string id,ConfigRequest request,CancellationToken ct)
    {
        if(!RemoteDocker.ConfigFiles.Contains(request.File))throw new ArgumentException("Недопустимый файл конфигурации.");
        var uuid=Managed(id).Uuid;var path="/SquadGame/ServerConfig/"+request.File;
        if(request.Content!=null){if(request.Content.Length>20000 || request.Content.Contains('\0'))throw new ArgumentException("Недопустимый размер конфигурации.");if(request.File=="Server.cfg")SquadConfig.Validate(request.Content);await api.WriteFile(uuid,path,Encoding.UTF8.GetBytes(request.Content),ct);return request.Content;}
        try{return Encoding.UTF8.GetString(await api.ReadFile(uuid,path,ct)).TrimStart('\uFEFF');}
        catch(HttpRequestException e) when(e.StatusCode==HttpStatusCode.NotFound){return StandardConfigs.Copy().GetValueOrDefault(request.File)??"";}
    }
    private async Task<List<(string Path,RespawnFile File)>> Entries(string id,CancellationToken ct)
    {
        var result=new List<(string,RespawnFile)>();var uuid=Managed(id).Uuid;int visited=0;
        async Task Walk(string root,string relative,int depth)
        {
            if(depth>12)throw new IOException("Слишком глубокая структура файлов.");
            List<RespawnFile> files;
            try{files=await api.ListFiles(uuid,root,ct);}catch(InvalidOperationException e) when(e.Message.Contains("HTTP 404")){return;}
            foreach(var file in files.Where(f=>!f.Symlink))
            {
                if(++visited>20000)throw new IOException("Слишком много файлов.");
                var path=relative+file.Name;
                if(file.File)result.Add((path,file));else await Walk(root+"/"+file.Name,path+"/",depth+1);
            }
        }
        if(!(await api.ListFiles(uuid,"/SquadGame",ct)).Any(f=>!f.File && f.Name=="Saved"))return result;
        var roots=await api.ListFiles(uuid,"/SquadGame/Saved",ct);
        foreach(var root in roots.Where(f=>!f.File && !f.Symlink && f.Name is "Logs" or "OSI_Scoreboards"))await Walk("/SquadGame/Saved/"+root.Name,root.Name+"/",0);
        return result;
    }
    private static string RemoteFile(string path)
    {
        RemoteDocker.ValidateFilePath(path);return "/SquadGame/Saved/"+path;
    }
    public async Task<List<ServerFile>> Files(string host,string ip,string id,CancellationToken ct)=>(await Entries(id,ct)).Select(e=>new ServerFile(e.Path,e.File.Bytes)).ToList();
    public Task<byte[]> Download(string host,string ip,string id,string path,CancellationToken ct)=>api.ReadFile(Managed(id).Uuid,RemoteFile(path),ct);
    public async Task<string> Logs(string host,string ip,string id,CancellationToken ct)
    {var bytes=await Download(host,ip,id,"Logs/SquadGame.log",ct);var text=Encoding.UTF8.GetString(bytes);return text.Length>12000?text[^12000..]:text;}
    public async Task<LogBatch> MatchLogs(string host,string ip,Instance instance,CancellationToken ct)
    {
        var positions=new Dictionary<string,LogPosition>(instance.LogPositions);var output=new StringBuilder();
        foreach(var entry in (await Entries(instance.Id,ct)).Where(e=>e.Path.StartsWith("Logs/")&&e.Path.EndsWith(".log",StringComparison.OrdinalIgnoreCase)))
        {
            var bytes=await Download(host,ip,instance.Id,entry.Path,ct);var old=positions.GetValueOrDefault(entry.Path);
            var offset=old!=null && old.Inode==entry.File.Created && old.Offset<=bytes.Length?old.Offset:0;
            output.Append(Encoding.UTF8.GetString(bytes.AsSpan((int)offset)));positions[entry.Path]=new(bytes.Length,entry.File.Created);
        }
        return new(output.ToString(),positions);
    }
    public async Task<Stream> ArchiveStream(string host,string ip,string id,CancellationToken ct)
    {
        var directory=Path.Combine(store.DirectoryPath,"archives");Directory.CreateDirectory(directory);
        var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.ReadWrite,Share=FileShare.Read,Options=FileOptions.DeleteOnClose};
        if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        var stream=new FileStream(Path.Combine(directory,"respawn-"+Guid.NewGuid().ToString("N")),options);
        try
        {
            long total=0;
            var entries=await Files(host,ip,id,ct);
            using(var gzip=new GZipStream(stream,CompressionLevel.Fastest,true))
            {
                // TarWriter emits no bytes for zero entries; an empty tar still needs its end blocks.
                if(entries.Count==0)await gzip.WriteAsync(new byte[1024],ct);
                else
                {
                    using var tar=new TarWriter(gzip,true);
                    foreach(var entry in entries)
                    {
                        var fileOptions=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.ReadWrite,Options=FileOptions.DeleteOnClose};if(!OperatingSystem.IsWindows())fileOptions.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
                        await using var data=new FileStream(Path.Combine(directory,"respawn-file-"+Guid.NewGuid().ToString("N")),fileOptions);
                        await api.CopyFile(Managed(id).Uuid,RemoteFile(entry.Path),data,ct);total+=data.Length;if(total>2L*1024*1024*1024)throw new IOException("Архив больше 2 GiB.");
                        data.Position=0;await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile,entry.Path){DataStream=data},ct);
                    }
                }
            }
            stream.Position=0;return stream;
        }
        catch{stream.Dispose();throw;}
    }
    public async Task<byte[]> Archive(string host,string ip,string id,CancellationToken ct)
    {await using var source=await ArchiveStream(host,ip,id,ct);using var target=new MemoryStream();await source.CopyToAsync(target,ct);return target.ToArray();}
    public async Task RestoreFiles(string host,string ip,string id,string path,CancellationToken ct)
    {
        await using var file=File.OpenRead(path);BackupFormat.ValidateFiles(file);file.Position=0;
        using var gzip=new GZipStream(file,CompressionMode.Decompress);using var tar=new TarReader(gzip);
        while(await tar.GetNextEntryAsync(cancellationToken:ct) is {} entry)
        {if(entry.DataStream==null)continue;RemoteDocker.ValidateFilePath(entry.Name);await api.WriteFileStream(Managed(id).Uuid,RemoteFile(entry.Name),entry.DataStream,entry.Length,ct);}
    }
    private static bool Public(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();if(IPAddress.IsLoopback(address)||address.IsIPv6LinkLocal||address.IsIPv6SiteLocal||address.Equals(IPAddress.Any)||address.Equals(IPAddress.IPv6Any))return false;
        var b=address.GetAddressBytes();if(b.Length==4)return b[0]!=10 && b[0]!=127 && b[0]!=0 && !(b[0]==172&&b[1]>=16&&b[1]<=31) && !(b[0]==192&&b[1]==168) && !(b[0]==169&&b[1]==254) && !(b[0]==100&&b[1]>=64&&b[1]<=127) && b[0]<224;
        return (b[0]&0xfe)!=0xfc && b[0]!=0xff;
    }
    public async Task<string> Command(string host,string ip,Instance instance,string command,CancellationToken ct)
    {
        RconCommands.Validate(command);var gate=gates.GetOrAdd(instance.Id,_=>new(1));await gate.WaitAsync(ct);
        try
        {
            if(!connections.TryGetValue(instance.Id,out var session))
            {
                var uuid=Managed(instance.Id).Uuid;var config=Encoding.UTF8.GetString(await api.ReadFile(uuid,"/SquadGame/ServerConfig/Rcon.cfg",ct));
                var info=RespawnHost.ParseConnection(await api.ConnectionInfo(uuid,ct),config,await api.Details(uuid,ct));
                if(execute==null){var addresses=await Dns.GetHostAddressesAsync(info.Host,ct);if(addresses.Length==0||addresses.Any(a=>!Public(a)))throw new IOException("Недопустимый адрес RCON от провайдера.");info=info with{Host=addresses[0].ToString()};}
                session=(info,new RconConnection());connections[instance.Id]=session;
            }
            try{return execute!=null?await execute(session.Info,command,ct):await session.Rcon.Execute(session.Info.Host,session.Info.RconPort,session.Info.Password,command,ct);}
            catch{Forget(instance.Id);throw;}
        }
        finally{gate.Release();}
    }
    public async Task<GameStatus?> Probe(string host,string ip,Instance instance,CancellationToken ct)
    {
        var server=(await api.Servers(ct)).Find(s=>s.Id==Managed(instance.Id).Uuid);
        if(server?.State!="running")
        {
            var pending=store.Load<Dictionary<string,string>>("respawn-power-pending")??[];
            return new(server?.State is "installing"?"gameInstalling":server?.State=="starting" || pending.GetValueOrDefault(Managed(instance.Id).Uuid)=="start"?"gameStarting":"gameUnavailable");
        }
        // Provider runtime and RCON readiness are separate facts. A missing RCON
        // password or allocation must not turn a running server into endless startup.
        try{var reply=await Command(host,ip,instance,"ListPlayers",ct);if(RconCommands.Rejected(reply) || !reply.Contains("Active Players",StringComparison.OrdinalIgnoreCase))return new("running");return new("gameReady");}
        catch(Exception) when(!ct.IsCancellationRequested){return new("running");}
    }
    public async Task<MetricSample> Metrics(string host,string ip,Instance instance,CancellationToken ct)
    {
        var root=await api.Resources(Managed(instance.Id).Uuid,ct);var attributes=root.TryGetProperty("attributes",out var a)?a:root;var resources=attributes.GetProperty("resources");
        return new(DateTimeOffset.UtcNow,resources.GetProperty("cpu_absolute").GetDouble(),resources.GetProperty("memory_bytes").GetInt64(),Managed(instance.Id).Plan.MemoryMb*1024L*1024,resources.GetProperty("disk_bytes").GetInt64(),null,null,null);
    }
}
