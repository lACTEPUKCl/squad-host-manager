using System.Text.RegularExpressions;
using SquadHostManager.Core;

namespace SquadHostManager.Service;

public sealed partial class PoolCoordinator
{
    private string MatchArchivePath(string id)
    {
        if(!Regex.IsMatch(id,"^[a-f0-9]{32}$"))throw new ArgumentException("Неверный архив.");
        return Path.Combine(store.DirectoryPath,"match-archives",id+".tar.gz");
    }
    private async Task PreserveMatchArchive(Instance inst,string operationId,CancellationToken ct)
    {
        if(inst.LastArchiveId is {} previous && inst.ArchivedStartedAt==inst.StartedAt && File.Exists(MatchArchivePath(previous)))return;
        var path=MatchArchivePath(operationId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary=path+".part";
        try
        {
            if(!File.Exists(path))
            {
                if(File.Exists(temporary))File.Delete(temporary);
                await using(var source=await infra.ArchiveStream(state.HostId!,state.Ip!,inst.Id,ct))
                await using(var target=NewPrivateFile(temporary,System.IO.FileAccess.Write))
                {
                    await BackupFormat.Copy(source,target,2L*1024*1024*1024,ct);
                    await target.FlushAsync(ct);target.Flush(true);
                }
                File.Move(temporary,path);
            }
            lock(gate)
            {
                var pending=state.Matches.Where(m=>m.OwnerId==inst.OwnerId && m.Value.InstanceId==inst.Id && m.Value.ArchiveId==null).ToList();
                foreach(var match in pending)
                {
                    var index=state.Matches.IndexOf(match);
                    state.Matches[index]=match with{Value=match.Value with{ArchiveId=operationId,ServerName=match.Value.ServerName??inst.Name}};
                }
                if(pending.Count==0)state.Matches.Add(new(inst.OwnerId,new(operationId,inst.Id,DateTimeOffset.UtcNow,inst.StartLayer,null,null,[],inst.Name,operationId)));
                inst.LastArchiveId=operationId;inst.ArchivedStartedAt=inst.StartedAt;
                Notify(inst.OwnerId,"noticeArchive",inst.Id,inst.Name);Save();
            }
        }
        catch(Exception e) when(e is not OperationCanceledException)
        {throw new IOException("Не удалось сохранить логи. Сервер и аренда не отключены. Повторите отключение: "+e.Message,e);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public Stream DownloadMatchArchive(User user,string id)
    {
        lock(gate)
        {
            var match=state.Matches.Find(m=>m.OwnerId==user.Id && m.Value.Id==id)?.Value??throw new UnauthorizedAccessException("Архив недоступен.");
            if(match.ArchiveId==null)throw new InvalidOperationException("Архив будет сохранён перед отключением сервера.");
            return new FileStream(MatchArchivePath(match.ArchiveId),FileMode.Open,System.IO.FileAccess.Read,FileShare.Read|FileShare.Delete);
        }
    }
    public void DeleteMatch(User user,string id)
    {
        lock(gate)
        {
            var match=state.Matches.Find(m=>m.OwnerId==user.Id && m.Value.Id==id)??throw new UnauthorizedAccessException("Запись недоступна.");
            state.Matches.Remove(match);state.DeletedMatches.Add(id);Save();
            if(match.Value.ArchiveId is {} archive && !state.Matches.Any(m=>m.Value.ArchiveId==archive))
            {var path=MatchArchivePath(archive);if(File.Exists(path))File.Delete(path);}
        }
    }
}
