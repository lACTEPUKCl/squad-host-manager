using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SquadHostManager.Core;

namespace SquadHostManager.Service;

public static class BackupFormat
{
    public const long MaxBytes=512L*1024*1024;
    public static async Task Copy(Stream input,Stream output,long limit,CancellationToken ct)
    {
        var buffer=new byte[65536];long total=0;int read;
        while((read=await input.ReadAsync(buffer,ct))>0){total+=read;if(total>limit)throw new ArgumentException("Копия слишком большая (максимум 512 MiB).");await output.WriteAsync(buffer.AsMemory(0,read),ct);}
    }
    public static BackupManifest ReadManifest(ZipArchive zip)
    {
        if(zip.Entries.Count!=2 || zip.Entries.Count(e=>e.FullName=="manifest.json")!=1 || zip.Entries.Count(e=>e.FullName=="files.tar.gz")!=1)throw new ArgumentException("Неверный формат резервной копии.");
        var entry=zip.GetEntry("manifest.json")!;if(entry.Length>2*1024*1024)throw new ArgumentException("Неверный размер настроек.");
        using var stream=entry.Open();var manifest=JsonSerializer.Deserialize<BackupManifest>(stream)??throw new ArgumentException("Нет настроек копии.");
        if(manifest.Format!=1 || manifest.Name is null || manifest.Name.Length is <1 or >80 || manifest.Configs is null || manifest.Configs.Count>RemoteDocker.ConfigFiles.Length || manifest.Configs.Any(c=>!RemoteDocker.ConfigFiles.Contains(c.Key) || c.Value is null || c.Value.Length>20000 || c.Value.Contains('\0')))throw new ArgumentException("Недопустимые настройки копии.");
        if(manifest.Configs.TryGetValue("Server.cfg",out var mainConfig))SquadConfig.Validate(mainConfig);
        var s=manifest.Settings;if(s is null || s.Name is null || s.Name.Length is <3 or >80 || s.Mods is null || (s.Password?.Length??0)>128 || s.MaxPlayers is <1 or >100 || s.Mods.Length>500 || (s.StartLayer?.Length??0)>200)throw new ArgumentException("Недопустимые параметры сервера.");
        return manifest;
    }
    public static void ValidateFiles(Stream input)
    {
        using var gzip=new GZipStream(input,CompressionMode.Decompress,true);using var tar=new TarReader(gzip,true);long total=0;int count=0;var names=new HashSet<string>(StringComparer.Ordinal);
        while(tar.GetNextEntry() is {} entry)
        {
            RemoteDocker.ValidateFilePath(entry.Name);
            if(!names.Add(entry.Name) || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || ++count>20000 || entry.Length<0 || (total+=entry.Length)>2L*1024*1024*1024)throw new ArgumentException("Недопустимый файл в копии.");
        }
    }
}

public sealed partial class PoolCoordinator
{
    private string BackupPath(string id){if(!Regex.IsMatch(id,"^[a-f0-9]{32}$"))throw new ArgumentException("Неверная копия.");return Path.Combine(store.DirectoryPath,"backups",id+".shmbackup");}
    public ServerToolsView Tools(User user,string? id)
    {
        lock(gate)
        {
            if(id!=null)Owned(user,id);
            var discord=state.Discord.GetValueOrDefault(user.Id);
            return new(state.Backups.Where(b=>b.OwnerId==user.Id).Select(b=>b.Value).OrderByDescending(b=>b.At).ToList(),id==null?[]:state.Metrics.GetValueOrDefault(id)?.ToList()??[],state.Matches.Where(m=>m.OwnerId==user.Id).Select(m=>m.Value).OrderByDescending(m=>m.At).ToList(),new(discord?.Webhook.Length>0,discord?.Enabled==true,discord?.Language??"ru",discord?.LastError,discord?.LastDelivered));
        }
    }
    public async Task<BackupView> CreateBackup(User user,string id,BackupRequest request,CancellationToken ct)
    {
        if(request.Name.Trim().Length is <1 or >80)throw new ArgumentException("Название копии: 1–80 символов.");
        await worker.WaitAsync(ct);var backupId=Guid.NewGuid().ToString("N");var path=BackupPath(backupId);
        try
        {
            var inst=FileAccess(user,id);lock(gate)CheckBackupQuota(user);
            var configs=new Dictionary<string,string>();foreach(var file in RemoteDocker.ConfigFiles)configs[file]=await infra.Config(state.HostId!,state.Ip!,id,new(file),ct);
            var manifest=new BackupManifest(1,request.Name.Trim(),DateTimeOffset.UtcNow,new(inst.Name,inst.Password,inst.Mods,inst.MaxPlayers,inst.StartLayer),configs);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using(var output=NewPrivateFile(path,System.IO.FileAccess.ReadWrite))
            using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
            {
                using(var data=zip.CreateEntry("manifest.json").Open())JsonSerializer.Serialize(data,manifest);
                await using var archive=await infra.BackupArchiveStream(state.HostId!,state.Ip!,id,ct);
                await using var entry=zip.CreateEntry("files.tar.gz",CompressionLevel.NoCompression).Open();await BackupFormat.Copy(archive,entry,BackupFormat.MaxBytes-2*1024*1024,ct);
            }
            RestrictBackup(path);
            var view=new BackupView(backupId,manifest.Name,inst.Name,manifest.At,new FileInfo(path).Length);
            lock(gate){state.Backups.Add(new(user.Id,view));Save();}return view;
        }
        catch{if(File.Exists(path))File.Delete(path);throw;}
        finally{worker.Release();}
    }
    private static void RestrictBackup(string path){if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);}
    private static FileStream NewPrivateFile(string path,System.IO.FileAccess access)
    {var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=access,Share=FileShare.None};if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;return new(path,options);}
    private void CheckBackupQuota(User user)
    {var list=state.Backups.Where(b=>b.OwnerId==user.Id).ToList();if(list.Count>=20 || list.Sum(b=>b.Value.Bytes)>1536L*1024*1024)throw new InvalidOperationException("Лимит копий: 20 копий, 2 GiB. Скачайте и удалите старые.");}
    public async Task<BackupView> ImportBackup(User user,Stream input,CancellationToken ct)
    {
        await worker.WaitAsync(ct);var id=Guid.NewGuid().ToString("N");var path=BackupPath(id);
        try
        {
            lock(gate)CheckBackupQuota(user);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using(var target=NewPrivateFile(path,System.IO.FileAccess.Write))await BackupFormat.Copy(input,target,BackupFormat.MaxBytes,ct);
            using var zip=ZipFile.OpenRead(path);var manifest=BackupFormat.ReadManifest(zip);using(var files=zip.GetEntry("files.tar.gz")!.Open())BackupFormat.ValidateFiles(files);
            RestrictBackup(path);var view=new BackupView(id,manifest.Name,manifest.Settings.Name,manifest.At,new FileInfo(path).Length);
            lock(gate){state.Backups.Add(new(user.Id,view));Save();}return view;
        }
        catch{if(File.Exists(path))File.Delete(path);throw;}
        finally{worker.Release();}
    }
    public Stream ExportBackup(User user,string id){lock(gate){if(!state.Backups.Any(b=>b.OwnerId==user.Id && b.Value.Id==id))throw new UnauthorizedAccessException();return File.OpenRead(BackupPath(id));}}
    public void DeleteBackup(User user,string id)
    {lock(gate){if(!state.Backups.Any(b=>b.OwnerId==user.Id && b.Value.Id==id))throw new UnauthorizedAccessException();if(state.Instances.Any(i=>i.RestoreBackupId==id))throw new InvalidOperationException("Копия используется для восстановления.");File.Delete(BackupPath(id));state.Backups.RemoveAll(b=>b.OwnerId==user.Id && b.Value.Id==id);Save();}}
    public OperationView RestoreBackup(User user,string id,CopyRequest request)
    {
        lock(gate)
        {
            if(Existing(user,request.RequestId) is {} old)return View(old);
            if(!state.Backups.Any(b=>b.OwnerId==user.Id && b.Value.Id==id))throw new UnauthorizedAccessException();
            RequireExistingHost();using var zip=ZipFile.OpenRead(BackupPath(id));var m=BackupFormat.ReadManifest(zip);
            var op=Start(user,new(request.RequestId,request.Name,m.Settings.Password,m.Settings.Mods,m.Settings.MaxPlayers,StartLayer:m.Settings.StartLayer));
            var inst=state.Instances.Single(i=>i.Id==state.Operations.Single(o=>o.Id==op.Id).InstanceId);inst.TemplateConfigs=m.Configs;inst.RestoreBackupId=id;Save();return op;
        }
    }
    private void RequireExistingHost(){if(state.HostId==null || state.HostPhase!="ready")throw new InvalidOperationException("Сначала подключите существующий хост. Эта операция не создаёт аренду.");}
    public async Task<OperationView> Clone(User user,string id,CopyRequest request,CancellationToken ct)
    {
        await worker.WaitAsync(ct);
        try
        {
            lock(gate){if(Existing(user,request.RequestId) is {} old)return View(old);RequireExistingHost();}
            var inst=FileAccess(user,id);var configs=new Dictionary<string,string>();foreach(var file in RemoteDocker.ConfigFiles)configs[file]=await infra.Config(state.HostId!,state.Ip!,id,new(file),ct);
            lock(gate){RequireExistingHost();var op=Start(user,new(request.RequestId,request.Name,inst.Password,inst.Mods,inst.MaxPlayers,StartLayer:inst.StartLayer));state.Instances.Single(i=>i.Id==state.Operations.Single(o=>o.Id==op.Id).InstanceId).TemplateConfigs=configs;Save();return op;}
        }
        finally{worker.Release();}
    }
    private async Task RestoreSavedFiles(Instance inst,CancellationToken ct)
    {
        if(inst.RestoreBackupId is not {} id)return;
        var temp=Path.Combine(store.DirectoryPath,"restore-"+Guid.NewGuid().ToString("N"));
        try
        {
            using(var zip=ZipFile.OpenRead(BackupPath(id)))
            await using(var entry=zip.GetEntry("files.tar.gz")!.Open())
            await using(var output=NewPrivateFile(temp,System.IO.FileAccess.Write))await BackupFormat.Copy(entry,output,BackupFormat.MaxBytes,ct);
            await infra.RestoreFiles(state.HostId!,state.Ip!,inst.Id,temp,ct);
            lock(gate){inst.RestoreBackupId=null;Save();}
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private DateTimeOffset lastToolsSample;
    public async Task TickServerTools(CancellationToken ct)
    {
        if(DateTimeOffset.UtcNow-lastToolsSample<TimeSpan.FromSeconds(30) || !await worker.WaitAsync(0,ct))return;
        try
        {
            List<Instance> targets;string host,ip;
            lock(gate){if(state.HostPhase!="ready" || state.HostId==null || state.Ip==null)return;targets=state.Instances.Where(i=>i.Phase is "running" or "stopped" or "error").ToList();host=state.HostId;ip=state.Ip;lastToolsSample=DateTimeOffset.UtcNow;}
            foreach(var inst in targets)
            {
                MetricSample metric;
                try{metric=await infra.Metrics(host,ip,inst,ct);}catch(Exception e) when(e is not OperationCanceledException){metric=new(DateTimeOffset.UtcNow,null,null,null,null,null,null,null,"unavailable");}
                lock(gate){var samples=state.Metrics.GetValueOrDefault(inst.Id)??[];samples.Add(metric);state.Metrics[inst.Id]=samples.Where(s=>s.At>DateTimeOffset.UtcNow.AddDays(-1)).TakeLast(2880).ToList();}
                try
                {
                    var files=await infra.Files(host,ip,inst.Id,ct);
                    var batch=await infra.MatchLogs(host,ip,inst,ct);var logs=batch.Text;
                    lock(gate)
                    {
                        var round=state.Rounds.GetValueOrDefault(inst.Id)??new();
                        foreach(var match in MatchHistory.Parse(inst.Id,logs,round,files.Where(f=>f.Path.StartsWith("Logs/",StringComparison.Ordinal)).ToList()))if(!state.DeletedMatches.Contains(match.Id) && !state.Matches.Any(m=>m.Value.Id==match.Id))state.Matches.Add(new(inst.OwnerId,match with{ServerName=inst.Name}));
                        state.Rounds[inst.Id]=round;
                        inst.LogPositions=batch.Positions;
                        foreach(var file in files.Where(f=>f.Path.StartsWith("OSI_Scoreboards/",StringComparison.Ordinal)))
                        {
                            var id=Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inst.Id+"|"+file.Path)));
                            if(state.DeletedMatches.Contains(id))continue;
                            var existing=state.Matches.FindIndex(m=>m.Value.Id==id);
                            if(existing<0)state.Matches.Add(new(inst.OwnerId,new(id,inst.Id,DateTimeOffset.UtcNow,null,null,null,[file],inst.Name)));
                            else state.Matches[existing]=new(inst.OwnerId,state.Matches[existing].Value with{Files=[file]});
                        }
                        foreach(var old in state.Matches.Where(m=>m.Value.InstanceId==inst.Id && m.Value.ArchiveId==null).OrderByDescending(m=>m.Value.At).Skip(500).ToList())state.Matches.Remove(old);
                    }
                }
                catch(Exception e) when(e is not OperationCanceledException){}
            }
            lock(gate)Save();
        }
        finally{worker.Release();}
    }
}

public static class MatchHistory
{
    public static List<MatchRecord> Parse(string instance,string logs,RoundObservation round,List<ServerFile>? files=null)
    {
        var result=new List<MatchRecord>();
        foreach(var line in logs.Split('\n'))
        {
            var time=Regex.Match(line,@"^\[(\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}(?::\d{3})?)\]");
            if(!DateTimeOffset.TryParseExact(time.Groups[1].Value,new[]{"yyyy.MM.dd-HH.mm.ss","yyyy.MM.dd-HH.mm.ss:fff"},System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out var at) || at<round.LastAt)continue;
            var id=Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(instance+"|"+line.Trim())));
            if(round.Seen.Contains(id))continue;round.Seen.Add(id);round.Seen=round.Seen.TakeLast(1024).ToList();round.LastAt=at;
            var world=Regex.Match(line,@"LogWorld: Bringing World /[A-Za-z0-9]+/(?:Maps/)?[A-Za-z0-9-]+/(?:.+/)?([A-Za-z0-9_-]+)\.");
            if(world.Success && world.Groups[1].Value!="TransitionMap"){round.Layer=world.Groups[1].Value;round.StartedAt=at;round.Result=null;}
            if(line.Contains("Match State Changed from WaitingToStart to InProgress",StringComparison.Ordinal))round.StartedAt=at;
            var winner=Regex.Match(line,@"DetermineMatchWinner\(\): (?:(The game was a draw) on (.+)|(.+) won on (.+))");
            if(winner.Success){round.Result=winner.Groups[1].Success?"draw":winner.Groups[3].Value;round.Layer=winner.Groups[1].Success?winner.Groups[2].Value:winner.Groups[4].Value;}
            if(line.Contains("LogGameState: Match State Changed from InProgress to WaitingPostMatch",StringComparison.Ordinal))
            {result.Add(new(id,instance,at,round.Layer,round.Result,round.StartedAt is {} start && start<=at?(at-start).TotalSeconds:null,files??[]));round.StartedAt=null;round.Result=null;}
        }
        return result;
    }
}


