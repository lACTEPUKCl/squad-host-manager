using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using SquadHostManager.Core;
namespace SquadHostManager.Service;
public sealed class RespawnApiException(System.Net.HttpStatusCode status):InvalidOperationException($"RespawnHost: HTTP {(int)status}. Проверьте состояние в панели перед повтором.")
{public System.Net.HttpStatusCode StatusCode{get;}=status;}

public sealed partial class RespawnHost(Store store,IConfiguration config,HttpClient? transport=null,Func<string,CancellationToken,Task<IRespawnInstallMonitor>>? monitorFactory=null):IDisposable
{
    private readonly HttpClient http=transport??new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(45)};
    private string TokenPath=>Path.Combine(store.DirectoryPath,"respawn-token.txt");
    private string Token=>File.Exists(TokenPath)?File.ReadAllText(TokenPath).Trim():config["Respawn:Token"]??"";
    public bool Configured=>Regex.IsMatch(Token,"^rsp_[a-fA-F0-9]{64}$");
    public bool Pending=>store.Load<string>("respawn-pending-order")!=null;
    private static string? Text(JsonElement e,string key)=>e.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString():null;
    private async Task<JsonElement> Send(HttpMethod method,string path,object? body,CancellationToken ct,bool authenticate=true)
    {
        using var request=new HttpRequestMessage(method,"https://respawnhost.com"+path);
        request.Headers.Accept.ParseAdd("application/json");
        if(authenticate){if(!Configured)throw new InvalidOperationException("Добавьте API-ключ RespawnHost.");request.Headers.Add("X-Api-Key",Token);}
        if(body!=null)request.Content=JsonContent.Create(body);
        using var response=await http.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode)throw new RespawnApiException(response.StatusCode);
        if(response.StatusCode==System.Net.HttpStatusCode.NoContent)return JsonSerializer.SerializeToElement(new{});
        var text=await response.Content.ReadAsStringAsync(ct);
        if(string.IsNullOrWhiteSpace(text))return JsonSerializer.SerializeToElement(new{});
        using var doc=JsonDocument.Parse(text);return doc.RootElement.Clone();
    }
    public async Task SaveToken(string token,CancellationToken ct)
    {
        if(Pending || Configured && token!=Token && await HasServers(ct))throw new InvalidOperationException("Сначала остановите серверы и завершите проверку заказов.");
        if(!Regex.IsMatch(token,"^rsp_[a-fA-F0-9]{64}$"))throw new ArgumentException("Неверный формат API-ключа.");
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://respawnhost.com/api/v1/servers?limit=1");request.Headers.Add("X-Api-Key",token);
        using var response=await http.SendAsync(request,ct);if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"RespawnHost: HTTP {(int)response.StatusCode}.");
        using var check=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if(!check.RootElement.TryGetProperty("data",out var entries) || entries.ValueKind!=JsonValueKind.Array)throw new InvalidOperationException("RespawnHost: неверный ответ проверки ключа.");
        store.Save("respawn-account-connected",true);
        var temporary=TokenPath+"."+Guid.NewGuid().ToString("N");
        try
        {
            var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write};if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            await using(var file=new FileStream(temporary,options))await using(var writer=new StreamWriter(file)){await writer.WriteAsync(token);await writer.FlushAsync(ct);file.Flush(true);}
            File.Move(temporary,TokenPath,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}

    }
    public async Task<List<RespawnPlan>> Plans(CancellationToken ct)
    {
        var data=await Send(HttpMethod.Get,"/api/games/short/squad/packages",null,ct,false);
        if(data.ValueKind==JsonValueKind.Object)data=data.GetProperty("data");
        return data.EnumerateArray().Select(p=>new RespawnPlan(p.GetProperty("id").GetInt32(),p.GetProperty("memory").GetInt32(),p.GetProperty("priceHourly").GetDecimal())).Where(p=>p.MemoryMb==8192 && p.HourlyPrice>0).OrderBy(p=>p.MemoryMb).ToList();
    }
    public async Task<List<RespawnServer>> Servers(CancellationToken ct)
    {
        var result=new List<RespawnServer>();
        for(var page=1;page<=100;page++)
        {
            var data=await Send(HttpMethod.Get,$"/api/v1/servers?limit=100&page={page}",null,ct);
            var rows=data.GetProperty("data").EnumerateArray().ToList();
            var transitions=store.Load<Dictionary<string,string>>("respawn-power-pending")??[];
            foreach(var row in rows)
            {
                var id=Text(row,"panelUuid")??throw new InvalidOperationException("RespawnHost: сервер без UUID.");
                if(!Guid.TryParse(id,out _))throw new InvalidOperationException("RespawnHost: неверный UUID.");
                var status=Text(row,"dockerState")??"unknown";
                result.Add(new(id,Text(row,"alias")??id,status));
                if(transitions.TryGetValue(id,out var target) && (target=="stop" && status is "offline" or "stopped" || target!="stop" && status=="running"))transitions.Remove(id);
            }
            store.Save("respawn-power-pending",transitions);
            if(data.TryGetProperty("summary",out var summary)&&summary.TryGetProperty("total",out var total))
            {
                if(total.GetInt32()<result.Count)throw new InvalidOperationException("RespawnHost: несогласованный список серверов.");
                if(result.Count==total.GetInt32())return result;
            }
            if(data.TryGetProperty("summary",out summary)&&summary.TryGetProperty("total",out total))
            {
                if(rows.Count==0)throw new InvalidOperationException("RespawnHost: неполный список серверов.");
            }
            else if(rows.Count<100)return result;
            if(result.Select(s=>s.Id).Distinct().Count()!=result.Count)throw new InvalidOperationException("RespawnHost: повторная страница серверов.");
        }
        throw new InvalidOperationException("Не удалось проверить весь список серверов RespawnHost.");
    }
    public async Task<bool> HasServers(CancellationToken ct)
    {
        if(Pending)return true;
        if(!Configured)
        {
            if(store.Load<bool>("respawn-account-connected"))throw new InvalidOperationException("Нужен API-ключ для проверки RespawnHost.");
            return false;
        }
        store.Save("respawn-account-connected",true);
        var servers=await Servers(ct);
        return (store.Load<Dictionary<string,string>>("respawn-power-pending")?.Count??0)>0 || servers.Any(s=>s.State is not ("offline" or "stopped"));
    }
    public async Task<RespawnView> View(CancellationToken ct)=>new(Configured,await Plans(ct),Configured?await Servers(ct):[],Pending);
    public async Task<JsonElement> Rent(RespawnRentRequest request,CancellationToken ct)
    {
        if(request.RequestId==Guid.Empty)throw new ArgumentException("Нужен уникальный RequestId.");
        var previous=store.Load<string>("respawn-order-"+request.RequestId);
        if(Pending || previous is not (null or "rejected"))throw new InvalidOperationException("Заказ уже отправлен или требует сверки в панели RespawnHost. Не повторяйте аренду.");
        var plan=(await Plans(ct)).SingleOrDefault(p=>p.Id==request.PlanId);
        if(plan==null || plan.HourlyPrice!=request.HourlyPrice)throw new InvalidOperationException("Тариф изменился. Обновите список.");
        if(request.Region != "eu")throw new ArgumentException("Неверный регион.");
        if(!Configured)throw new InvalidOperationException("Добавьте API-ключ RespawnHost.");
        store.Save("respawn-order-"+request.RequestId,"sent");store.Save("respawn-pending-order",request.RequestId.ToString());
        // Never retry a paid POST: timeout/unknown reply leaves the order blocked for reconciliation.
        JsonElement response;
        try{response=await Send(HttpMethod.Post,"/api/v1/servers/rent",new{game_short="squad",plan_id=request.PlanId,region=request.Region,billing_mode="hourly",instance_count=1,performance_tier="performance"},ct);}
        catch(RespawnApiException error) when(error.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.PaymentRequired or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.UnprocessableEntity or System.Net.HttpStatusCode.TooManyRequests)
        {
            store.Save("respawn-order-"+request.RequestId,"rejected");store.Delete("respawn-pending-order");throw;
        }
        var server=response.TryGetProperty("data",out var wrapped)?wrapped:response;
        if(!Guid.TryParse(Text(server,"panelUuid"),out _))throw new InvalidOperationException("Заказ отправлен. Проверьте его результат в панели RespawnHost.");
        var transitions=store.Load<Dictionary<string,string>>("respawn-power-pending")??[];transitions[Text(server,"panelUuid")!]="start";store.Save("respawn-power-pending",transitions);
        store.Save("respawn-order-result-"+request.RequestId,response);
        store.Save("respawn-order-"+request.RequestId,"done");store.Delete("respawn-pending-order");return response;
    }
    public async Task Power(string id,string state,CancellationToken ct)
    {
        if(!Guid.TryParse(id,out _))throw new ArgumentException("Неверный UUID сервера.");
        if(state is not ("start" or "stop" or "restart"))throw new ArgumentException("Неверное действие.");
        if(!(await Servers(ct)).Any(s=>s.Id==id))throw new UnauthorizedAccessException("Сервер недоступен этому аккаунту.");
        var transitions=store.Load<Dictionary<string,string>>("respawn-power-pending")??[];transitions[id]=state;store.Save("respawn-power-pending",transitions);
        await Send(HttpMethod.Post,$"/api/v1/servers/{id}/powerstate",new{power_state=state},ct);
    }
    public void Dispose(){if(transport==null)http.Dispose();}
}
