using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SquadHostManager.Core;
namespace SquadHostManager.Service;

public record RespawnFile(string Name,long Bytes,bool File,bool Symlink,long Created=0);
public record RespawnConnection(string Host,int GamePort,int RconPort,string Password);
public sealed partial class RespawnHost
{
    private static void Id(string id){if(!Guid.TryParse(id,out _))throw new ArgumentException("Неверный UUID сервера.");}
    public static void CheckPath(string path)
    {
        if(path.Length>1024 || !path.StartsWith("/SquadGame/",StringComparison.Ordinal) || path.Contains('\\') || path.Any(char.IsControl) || path.Split('/').Any(p=>p is "." or ".."))throw new ArgumentException("Недопустимый путь файла.");
    }
    private async Task<HttpResponseMessage> FileRequest(HttpMethod method,string id,string path,byte[]? content,CancellationToken ct)
    {
        Id(id);CheckPath(path);if(!Configured)throw new InvalidOperationException("Добавьте API-ключ RespawnHost.");
        using var request=new HttpRequestMessage(method,$"https://respawnhost.com/api/v1/servers/{id}/files/{(method==HttpMethod.Get?"content":"write")}?file="+Uri.EscapeDataString(path));
        request.Headers.Add("X-Api-Key",Token);
        if(content!=null){request.Content=new ByteArrayContent(content);request.Content.Headers.ContentType=new("text/plain");}
        var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode){var status=response.StatusCode;response.Dispose();throw new HttpRequestException($"RespawnHost files: HTTP {(int)status}.",null,status);}
        return response;
    }
    public async Task CopyFile(string id,string path,Stream target,CancellationToken ct)
    {
        using var response=await DownloadResponse(id,path,ct);
        if(response.Content.Headers.ContentLength>2L*1024*1024*1024)throw new IOException("Файл больше 2 GiB.");
        await using var source=await response.Content.ReadAsStreamAsync(ct);await BackupFormat.Copy(source,target,2L*1024*1024*1024,ct);
    }
    public async Task WriteFileStream(string id,string path,Stream source,long length,CancellationToken ct)
    {
        Id(id);CheckPath(path);if(length>2L*1024*1024*1024)throw new ArgumentException("Файл больше 2 GiB.");
        using var request=new HttpRequestMessage(HttpMethod.Post,$"https://respawnhost.com/api/v1/servers/{id}/files/write?file="+Uri.EscapeDataString(path));request.Headers.Add("X-Api-Key",Token);
        request.Content=new StreamContent(source);request.Content.Headers.ContentType=new("application/octet-stream");request.Content.Headers.ContentLength=length;
        using var response=await http.SendAsync(request,ct);if(!response.IsSuccessStatusCode)throw new IOException($"RespawnHost files: HTTP {(int)response.StatusCode}.");
    }
    public async Task ApplyIdentity(string id,Instance instance,CancellationToken ct)
    {
        var values=new Dictionary<string,string>{{"SERVER_NAME",instance.Name},{"SERVER_PASSWORD",instance.Password},{"MAX_PLAYERS",instance.MaxPlayers.ToString(System.Globalization.CultureInfo.InvariantCulture)},{"servername",instance.Name},{"maxplayers",instance.MaxPlayers.ToString(System.Globalization.CultureInfo.InvariantCulture)},{"serverpassword",instance.Password}};
        foreach(var variable in Variables(await Startup(id,ct)).Where(v=>v.Editable&&values.ContainsKey(v.Key)))
            await Send(HttpMethod.Put,$"/api/v1/servers/{id}/startup-variable",new{key=variable.Key,value=values[variable.Key],language="en"},ct);
    }
    public async Task<byte[]> ReadFile(string id,string path,CancellationToken ct)
    {
        using var response=await DownloadResponse(id,path,ct);
        if(response.Content.Headers.ContentLength>128L*1024*1024)throw new IOException("Файл больше 128 MiB.");
        await using var source=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();await BackupFormat.Copy(source,output,128L*1024*1024,ct);return output.ToArray();
    }
    private async Task<HttpResponseMessage> DownloadResponse(string id,string path,CancellationToken ct)
    {
        try{return await FileRequest(HttpMethod.Get,id,path,null,ct);}
        catch(HttpRequestException e) when(e.StatusCode==HttpStatusCode.NotFound && path.StartsWith("/SquadGame/Saved/",StringComparison.Ordinal))
        {
            // The content endpoint can report 404 for an existing large log.
            // Use the provider's download token; never forward the account key to its node.
            var ticket=await Send(HttpMethod.Get,$"/api/v1/servers/{id}/files/token?file="+Uri.EscapeDataString(path),null,ct);
            if(!Uri.TryCreate(Text(ticket,"url"),UriKind.Absolute,out var url) || url.Scheme!="https" || url.UserInfo.Length!=0 || !(url.IsDefaultPort || url.Port==8080) ||
                !(url.Host.Equals("respawnhost.com",StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith(".respawnhost.com",StringComparison.OrdinalIgnoreCase)))
                throw new IOException("RespawnHost: недопустимый адрес скачивания файла.");
            using var request=new HttpRequestMessage(HttpMethod.Get,url);
            HttpResponseMessage response;
            try{response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);}
            catch(HttpRequestException){throw new IOException("RespawnHost: не удалось скачать файл.");}
            if(!response.IsSuccessStatusCode)
            {var status=response.StatusCode;response.Dispose();throw new HttpRequestException($"RespawnHost download: HTTP {(int)status}.",null,status);}
            return response;
        }
    }
    public async Task WriteFile(string id,string path,byte[] content,CancellationToken ct)
    {if(content.Length>128*1024*1024)throw new ArgumentException("Файл больше 128 MiB.");using var response=await FileRequest(HttpMethod.Post,id,path,content,ct);}
    public async Task<List<RespawnFile>> ListFiles(string id,string directory,CancellationToken ct)
    {
        Id(id);CheckPath(directory.EndsWith('/')?directory:directory+"/");var result=new List<RespawnFile>();
        for(var page=1;page<=100;page++)
        {
            var response=await Send(HttpMethod.Get,$"/api/v1/servers/{id}/files?directory="+Uri.EscapeDataString(directory)+$"&page={page}&per_page=500",null,ct);
            var container=response.TryGetProperty("entries",out var entries)?entries:response;var rows=container.GetProperty("data").EnumerateArray().ToList();
            foreach(var item in rows)
            {
                var a=item.TryGetProperty("attributes",out var attributes)?attributes:item;
                var name=Text(a,"name")??throw new IOException("Нет имени файла.");
                if(name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl))throw new IOException("Недопустимое имя файла от провайдера.");
                var created=DateTimeOffset.TryParse(Text(a,"created_at")??Text(a,"created"),out var at)?at.UtcTicks:0;
                result.Add(new(name,a.TryGetProperty("size",out var size)?size.GetInt64():0,(a.TryGetProperty("is_file",out var file)||a.TryGetProperty("file",out file))&&file.GetBoolean(),(a.TryGetProperty("is_symlink",out var link)||a.TryGetProperty("symlink",out link))&&link.GetBoolean(),created));
            }
            if(result.Select(f=>f.Name).Distinct().Count()!=result.Count)throw new IOException("Повторная страница файлов.");
            if(!container.TryGetProperty("total",out var total) || result.Count>=total.GetInt32())return result;
            if(rows.Count==0)throw new IOException("Неполный список файлов.");
        }
        throw new IOException("Слишком много страниц файлов.");
    }
    public async Task<JsonElement> Startup(string id,CancellationToken ct){Id(id);return await Send(HttpMethod.Get,$"/api/v1/servers/{id}/startup-information/en",null,ct);}
    public async Task WaitStartup(string id,CancellationToken ct,TimeSpan? timeout=null)
    {
        var until=DateTimeOffset.UtcNow+(timeout??TimeSpan.FromMinutes(15));
        while(true)
        {
            try{if(Variables(await Startup(id,ct)).Count>0)return;}
            catch(RespawnApiException e) when(e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable){}
            if(DateTimeOffset.UtcNow>=until)throw new TimeoutException("RespawnHost ещё готовит настройки сервера. Аренда сохранена; повторный заказ не требуется.");
            await Task.Delay(2000,ct);
        }
    }
    public async Task<IRespawnInstallMonitor> WatchInstall(string id,CancellationToken ct)
    {
        Id(id);if(monitorFactory!=null)return await monitorFactory(id,ct);
        var info=await Send(HttpMethod.Get,$"/api/v1/servers/{id}/websocket",null,ct);
        return await RespawnInstallMonitor.Open(info.GetProperty("url").GetString()!,info.GetProperty("token").GetString()!,ct);
    }
    public static List<(string Key,string Value,bool Editable)> Variables(JsonElement root)
    {
        if(!root.TryGetProperty("data",out var rows) && !root.TryGetProperty("variables",out rows) || rows.ValueKind!=JsonValueKind.Array)throw new IOException("RespawnHost: нет списка настроек запуска.");
        return rows.EnumerateArray().Select(row=>
        {
            var a=row.TryGetProperty("attributes",out var attributes)?attributes:row;
            var key=Text(a,"env_variable")??Text(a,"envVariable")??Text(a,"key")??throw new IOException("RespawnHost: нет ключа настройки запуска.");
            var value=Text(a,"server_value")??Text(a,"serverValue")??Text(a,"value")??"";
            var editable=!((a.TryGetProperty("is_editable",out var edit)||a.TryGetProperty("isEditable",out edit))&&edit.ValueKind==JsonValueKind.False);
            return(key,value,editable);
        }).ToList();
    }
    public async Task ApplyWorkshop(string id,string mods,CancellationToken ct)
    {
        var ids=System.Text.RegularExpressions.Regex.Matches(mods,@"\d+").Select(m=>m.Value).Distinct().ToArray();
        if(mods.Any(c=>!char.IsAsciiDigit(c)&&!char.IsWhiteSpace(c)&&c!=','))throw new ArgumentException("Недопустимые Workshop ID.");
        var variables=Variables(await Startup(id,ct));
        var variable=variables.FirstOrDefault(v=>new[]{"MODS","MOD_IDS","WORKSHOP_IDS","WORKSHOP_MODS","STEAM_WORKSHOP_IDS","SQUAD_MODS","WORKSHOP_ITEMS"}.Contains(v.Key) && v.Editable);
        if(variable.Key==null){if(ids.Length>0)throw new InvalidOperationException("RespawnHost не предоставил настройку Workshop для этого сервера. Моды не применены.");return;}
        var value=string.Join(variable.Key=="WORKSHOP_ITEMS" || variable.Value.Contains(',')?",":" ",ids);
        await Send(HttpMethod.Put,$"/api/v1/servers/{id}/startup-variable",new{key=variable.Key,value,language="en"},ct);
    }
    public async Task<JsonElement> ConnectionInfo(string id,CancellationToken ct){Id(id);return await Send(HttpMethod.Get,$"/api/v1/servers/{id}/connection-info?lang=en",null,ct);}
    public static RespawnConnection ParseConnection(JsonElement info,string rconConfig,JsonElement? details=null)
    {
        if(info.TryGetProperty("data",out var data)&&data.ValueKind==JsonValueKind.Object)info=data;
        var address=Text(info,"connectionString")??throw new InvalidOperationException("RespawnHost не предоставил адрес игры.");
        if(!Uri.TryCreate("tcp://"+address,UriKind.Absolute,out var uri)||uri.Port is <1 or >65535 || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath!="/" && uri.AbsolutePath!="")throw new IOException("Недопустимый адрес игры.");
        int? port=null;
        if(info.TryGetProperty("variables",out var variables)&&variables.ValueKind==JsonValueKind.Array)
            foreach(var v in variables.EnumerateArray())if((Text(v,"key")??"").Contains("RCON",StringComparison.OrdinalIgnoreCase)&&int.TryParse(Text(v,"value"),out var number))port=number;
        if(info.TryGetProperty("ports",out var ports)&&ports.ValueKind==JsonValueKind.Array)
            foreach(var p in ports.EnumerateArray())if((Text(p,"name")??Text(p,"label")??"").Contains("RCON",StringComparison.OrdinalIgnoreCase)&&p.TryGetProperty("port",out var n))port=n.GetInt32();
        if(details is {} server && server.TryGetProperty("panelServer",out var panel) && panel.TryGetProperty("allocations",out var allocations))
        {
            var line=rconConfig.ReplaceLineEndings("\n").Split('\n').Select(l=>l.Trim()).FirstOrDefault(l=>l.StartsWith("Port=",StringComparison.OrdinalIgnoreCase));
            if(int.TryParse(line?[5..],out var configured)&&allocations.EnumerateArray().Any(a=>a.GetProperty("port").GetInt32()==configured))port=configured;
        }
        if(port is null or <1 or >65535)throw new InvalidOperationException("RespawnHost не предоставил внешний порт RCON. Команда не отправлена.");
        var password=rconConfig.TrimStart('\uFEFF').Split('\n').Select(l=>l.Trim()).FirstOrDefault(l=>l.StartsWith("Password=",StringComparison.OrdinalIgnoreCase))?[9..];
        if(string.IsNullOrWhiteSpace(password))throw new InvalidOperationException("В Rcon.cfg не задан пароль.");
        return new(uri.Host,uri.Port,port.Value,password);
    }
    public async Task<JsonElement> Details(string id,CancellationToken ct){Id(id);return await Send(HttpMethod.Get,$"/api/v1/servers/{id}?includePanelServer=true",null,ct);}
    public async Task ConfigureRcon(string id,string password,CancellationToken ct,TimeSpan? timeout=null)
    {
        var until=DateTimeOffset.UtcNow+(timeout??TimeSpan.FromSeconds(config.GetValue<int?>("Respawn:RconSetupTimeoutSeconds")??120));
        List<int> ports=[];
        while(true)
        {
            var details=await Details(id,ct);
            if(details.TryGetProperty("panelServer",out var panel) && panel.ValueKind==JsonValueKind.Object && panel.TryGetProperty("allocations",out var allocations) && allocations.ValueKind==JsonValueKind.Array)
                ports=allocations.EnumerateArray().Where(a=>a.TryGetProperty("isPrimary",out var primary) && primary.ValueKind==JsonValueKind.False && a.TryGetProperty("port",out var port) && port.TryGetInt32(out var n) && n is >0 and <=65535).Select(a=>a.GetProperty("port").GetInt32()).Distinct().Order().ToList();
            if(ports.Count>0)break;
            if(DateTimeOffset.UtcNow>=until)throw new TimeoutException("RespawnHost не предоставил выделенный порт RCON. Запуск игры не выполнен. Повторите запуск после обновления данных провайдера.");
            await Task.Delay(TimeSpan.FromSeconds(2),ct);
        }
        var rconText=Encoding.UTF8.GetString(await ReadFile(id,"/SquadGame/ServerConfig/Rcon.cfg",ct)).TrimStart('\uFEFF').ReplaceLineEndings("\n");
        static string? Value(string text,string key)=>text.Split('\n').Select(l=>l.Trim().Split('=',2)).Where(parts=>parts.Length==2 && parts[0].Trim().Equals(key,StringComparison.OrdinalIgnoreCase)).Select(parts=>parts[1].Trim().Trim('"')).LastOrDefault();
        var port=int.TryParse(Value(rconText,"Port"),out var existing)&&ports.Contains(existing)?existing:ports[^1];
        var secret=string.IsNullOrWhiteSpace(Value(rconText,"Password"))?password:Value(rconText,"Password")!;
        if(string.IsNullOrWhiteSpace(secret) || secret.Length>1024 || secret.Any(char.IsControl))throw new InvalidOperationException("Не задан допустимый пароль RCON. Запуск игры не выполнен.");
        rconText=System.Text.RegularExpressions.Regex.Replace(rconText,@"(?im)^[ \t]*(IP|Port|Password)[ \t]*=.*(?:\n|$)","");
        foreach(var (key,value) in new[]{("IP","0.0.0.0"),("Port",port.ToString()),("Password",secret)})
        {
            rconText+="\n"+key+"="+value+"\n";
        }
        await WriteFile(id,"/SquadGame/ServerConfig/Rcon.cfg",Encoding.UTF8.GetBytes(rconText),ct);
        var saved=Encoding.UTF8.GetString(await ReadFile(id,"/SquadGame/ServerConfig/Rcon.cfg",ct));
        if(Value(saved,"IP")!="0.0.0.0" || Value(saved,"Port")!=port.ToString() || Value(saved,"Password")!=secret)
            throw new IOException("Проверка сохранённого Rcon.cfg не пройдена. Запуск игры не выполнен.");
    }
    public async Task<JsonElement> Resources(string id,CancellationToken ct){Id(id);return await Send(HttpMethod.Get,$"/api/v1/servers/{id}/resource-utilization",null,ct);}
    public async Task ReinstallKeepingFiles(string id,CancellationToken ct){Id(id);await Send(HttpMethod.Post,$"/api/v1/servers/{id}/reinstall",new{keep_files=true},ct);}
    public async Task Alias(string id,string name,CancellationToken ct){Id(id);await Send(HttpMethod.Put,$"/api/v1/servers/{id}/alias",new{alias=name},ct);}
}
