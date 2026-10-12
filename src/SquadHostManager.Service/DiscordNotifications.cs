using System.Net.Http.Json;
using System.Text.RegularExpressions;
using SquadHostManager.Core;

namespace SquadHostManager.Service;

public static class DiscordNotifications
{
    public static Uri Validate(string url)
    {
        if(!Regex.IsMatch(url,@"^https://discord\.com/api(?:/v10)?/webhooks/[0-9]{17,22}/[A-Za-z0-9_-]{30,200}$"))throw new ArgumentException("Скопируйте URL вебхука из настроек канала Discord.");
        return new Uri(url+"?wait=true");
    }
    public static bool Relevant(string code)=>code is "noticeHostPreparing" or "noticeSquadDownloading" or "noticeHostReady" or "noticeHostFailed" or "noticeReady" or "noticeCrashed" or "noticeRecovering" or "noticeRecoveryLimit" or "noticeServerStopped";
    public static string Message(string language,string code,string name)
    {
        if(code is "noticeHostPreparing" or "noticeSquadDownloading" or "noticeHostReady" or "noticeHostFailed")
        {
            var position=code switch{"noticeHostPreparing"=>0,"noticeSquadDownloading"=>1,"noticeHostReady"=>2,_=>3};
            var messages=language switch
            {
                "ru"=>new[]{"Cherry готовит хост","Скачивается Squad","Хост готов","Ошибка подготовки хоста"},
                "de"=>new[]{"Cherry bereitet Host vor","Squad wird heruntergeladen","Host bereit","Hostvorbereitung fehlgeschlagen"},
                "es"=>new[]{"Cherry prepara el host","Descargando Squad","Host listo","Error de preparación del host"},
                "fr"=>new[]{"Cherry prépare l’hôte","Téléchargement de Squad","Hôte prêt","Échec de préparation"},
                "zh-Hans"=>new[]{"Cherry 正在准备主机","正在下载 Squad","主机已就绪","主机准备失败"},
                "tr"=>new[]{"Cherry host hazırlıyor","Squad indiriliyor","Host hazır","Host hazırlığı başarısız"},
                _=>new[]{"Cherry is preparing the host","Downloading Squad","Host ready","Host preparation failed"}
            };
            return messages[position]+": "+new string(name.Where(c=>!char.IsControl(c)).Take(80).ToArray());
        }
        var index=code switch{"noticeReady"=>0,"noticeCrashed"=>1,"noticeRecovering"=>2,"noticeRecoveryLimit"=>3,_=>4};
        var labels=language switch
        {
            "de"=>new[]{"Server bereit","Server abgestürzt","Server wird wiederhergestellt","Wiederherstellungslimit erreicht","Server gestoppt"},
            "es"=>new[]{"Servidor listo","El servidor falló","Recuperando el servidor","Límite de recuperación alcanzado","Servidor detenido"},
            "fr"=>new[]{"Serveur prêt","Le serveur a planté","Récupération du serveur","Limite de récupération atteinte","Serveur arrêté"},
            "zh-Hans"=>new[]{"服务器已就绪","服务器崩溃","正在恢复服务器","已达到恢复次数限制","服务器已停止"},
            "tr"=>new[]{"Sunucu hazır","Sunucu çöktü","Sunucu kurtarılıyor","Kurtarma sınırına ulaşıldı","Sunucu durduruldu"},
            "ru"=>new[]{"Сервер готов","Сервер упал","Сервер восстанавливается","Лимит восстановления исчерпан","Сервер остановлен"},
            _=>new[]{"Server ready","Server crashed","Recovering server","Recovery limit reached","Server stopped"}
        };
        return labels[index]+": "+new string(name.Where(c=>!char.IsControl(c)).Take(80).ToArray());
    }
    public static async Task Send(string webhook,string message,CancellationToken ct,HttpClient? transport=null)
    {
        using var owned=transport==null?new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)}:null;
        using var response=await (transport??owned!).PostAsJsonAsync(Validate(webhook),new{content=message,allowed_mentions=new{parse=Array.Empty<string>()}},ct);
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException("Discord: HTTP "+(int)response.StatusCode);
    }
}
public sealed partial class PoolCoordinator
{
    public void SetDiscord(User user,DiscordSettings request)
    {
        if(!new[]{"ru","en","de","es","fr","zh-Hans","tr"}.Contains(request.Language))throw new ArgumentException("Неизвестный язык.");
        if(request.Webhook is {Length:>0})DiscordNotifications.Validate(request.Webhook);
        lock(gate)
        {
            var old=state.Discord.GetValueOrDefault(user.Id);
            var webhook=request.Webhook??old?.Webhook??"";
            if(request.Enabled && webhook.Length==0)throw new ArgumentException("Укажите вебхук Discord.");
            state.Discord[user.Id]=new(){Webhook=webhook,Enabled=request.Enabled,Language=request.Language,Delivered=old!=null && old.Webhook==webhook?old.Delivered.ToList():state.Notices.Where(n=>n.OwnerId==user.Id).Select(n=>n.Value.Id).ToList()};Save();
        }
    }
    public async Task TestDiscord(User user,CancellationToken ct)
    {
        string webhook;lock(gate)webhook=state.Discord.GetValueOrDefault(user.Id)?.Webhook??"";
        await DiscordNotifications.Send(webhook,"Squad Host Manager ✓",ct);
    }
    public async Task DeliverDiscord(CancellationToken ct,HttpClient? transport=null)
    {
        List<(string Owner,DiscordDelivery Settings,Notice Notice,string Name)> pending;
        lock(gate)
        {
            if(state.HostPhase=="ready" && state.HostId!=null)
            {
                var initial=state.Discord.Where(d=>d.Value.Enabled && d.Value.LastDelivered==null && !state.Notices.Any(n=>n.OwnerId==d.Key && DiscordNotifications.Relevant(n.Value.Code))).ToList();
                foreach(var entry in initial)Notify(entry.Key,"noticeHostReady",null,"Cherry "+state.HostId);
                if(initial.Count>0)Save();
            }
            pending=state.Discord.Where(d=>d.Value.Enabled && d.Value.RetryAt<=DateTimeOffset.UtcNow).SelectMany(d=>state.Notices.Where(n=>n.OwnerId==d.Key && DiscordNotifications.Relevant(n.Value.Code) && !d.Value.Delivered.Contains(n.Value.Id)).Take(3).Select(n=>(d.Key,d.Value,n.Value,state.Instances.Find(i=>i.Id==n.Value.InstanceId)?.Name??n.Value.Detail))).ToList();
        }
        foreach(var (owner,settings,notice,name) in pending)
        {
            lock(gate){if(state.Discord.GetValueOrDefault(owner)!=settings || !settings.Enabled || settings.RetryAt>DateTimeOffset.UtcNow)continue;}
            try
            {
                new Accounts(store).EnsureAccess(owner);
                await DiscordNotifications.Send(settings.Webhook,DiscordNotifications.Message(settings.Language,notice.Code,name),ct,transport);
                lock(gate){settings.Delivered.Add(notice.Id);settings.Delivered=settings.Delivered.TakeLast(200).ToList();settings.LastDelivered=DateTimeOffset.UtcNow;settings.LastError=null;Save();}
            }
            catch(Exception e) when(e is not OperationCanceledException)
            {lock(gate){settings.LastError=e is InvalidOperationException?e.Message:"Не удалось доставить уведомление в Discord.";settings.RetryAt=DateTimeOffset.UtcNow.AddMinutes(2);if(e is UnauthorizedAccessException)settings.Enabled=false;Save();}}
        }
    }
}
