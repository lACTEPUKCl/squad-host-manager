using SquadHostManager.Core;

namespace SquadHostManager.Service;

public sealed partial class PoolCoordinator
{
    public List<ConfigRevision> CommonConfigHistory(User user,string file)
    {
        if(!RemoteDocker.ConfigFiles.Contains(file))throw new ArgumentException("Недопустимый конфиг.");
        lock(gate)return store.Load<List<ConfigRevision>>("common-config-history-"+user.Id+"-"+file)??[];
    }
    private void RememberCommonConfig(User user,string file,string content)
    {
        var entries=CommonConfigHistory(user,file);
        if(entries.FirstOrDefault()?.Content==content)return;
        entries.Insert(0,new(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,content));
        store.Save("common-config-history-"+user.Id+"-"+file,entries.Take(40).ToList());
    }
    public string StandardConfig(User user,string file)
    {
        if(!RemoteDocker.ConfigFiles.Contains(file))throw new ArgumentException("Недопустимый конфиг.");
        lock(gate){var text=store.Load<Dictionary<string,string>>("game-standard-configs")?.GetValueOrDefault(file)??StandardConfigs.Get(file);return file=="Server.cfg"?StandardConfigs.SharedServer(text):text;}
    }
}
