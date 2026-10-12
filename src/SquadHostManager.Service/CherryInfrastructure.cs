using SquadHostManager.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SquadHostManager.Service;

public sealed class CherryInfrastructure : IInfrastructure, IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    public void Dispose() { docker.Dispose(); if (ownsHttp) http.Dispose(); }
    private readonly IConfiguration config;
    private readonly RemoteDocker docker;
    private readonly Store store;
    public CherryInfrastructure(IConfiguration config, Store store, HttpClient? client = null)
    {
        this.config = config; this.store = store; ownsHttp = client == null; docker = new(config, store, ServerKeys);
        http = client ?? new() { BaseAddress = new("https://api.cherryservers.com/v1/"), Timeout = TimeSpan.FromSeconds(45) };
    }
    private static string? ReadSecret(string? path) => string.IsNullOrEmpty(path) ? null : File.ReadAllText(path).Trim();
    private string Required(string key) => config[key] ?? throw new InvalidOperationException($"Администратор должен настроить {key}.");
    public async Task<BalanceView> Balance(CancellationToken ct)
    {
        var root=await Request(HttpMethod.Get,$"teams/{Required("Cherry:TeamId")}",null,ct)??throw new InvalidOperationException("Cherry team unavailable.");
        var credit=root.GetProperty("credit");var account=credit.GetProperty("account");var promo=credit.GetProperty("promo");
        return new(account.GetProperty("remaining").GetDecimal(),account.GetProperty("currency").GetString()!,promo.GetProperty("currency").GetString()==account.GetProperty("currency").GetString()?promo.GetProperty("remaining").GetDecimal():null,DateTimeOffset.UtcNow);
    }
    private async Task<HashSet<string>> ServerKeys(string hostId, CancellationToken ct)
    {
        var response = await Request(HttpMethod.Get, $"servers/{hostId}/ssh-keys", null, ct);
        var keys = response is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("key", out _) ? new[] { value } : Items(response, "ssh_keys");
        return keys.Select(k => SshKeyMaterial.Identity(Text(k, "key") ?? "")).ToHashSet(StringComparer.Ordinal);
    }
    private async Task<JsonElement?> Request(HttpMethod method, string path, object? body, CancellationToken ct, bool rental = false)
    {
        var token=config["Cherry:Token"]??ReadSecret(config["Cherry:TokenFile"]);
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Cherry API ещё не настроен администратором.");
        using var message = new HttpRequestMessage(method, path);
        message.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        if (body != null) message.Content = JsonContent.Create(body);
        // Never retry a POST that can charge the account.
        using var response = await http.SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.NotFound && method == HttpMethod.Get) return null;
        if (!response.IsSuccessStatusCode)
        {
            var error = $"Cherry API: HTTP {(int)response.StatusCode}. Проверьте аккаунт и журнал провайдера.";
            if (rental && (int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.RequestTimeout) throw new RentalRejectedException(error);
            throw new InvalidOperationException(error);
        }
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length == 0 ? null : JsonSerializer.Deserialize<JsonElement>(bytes);
    }
    private static IEnumerable<JsonElement> Items(JsonElement? root, string field)
    {
        if (root is not { } value) return [];
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().ToArray();
        if (value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) return data.EnumerateArray().ToArray();
        return value.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : [];
    }
    private static string? Text(JsonElement root, string field) => root.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;
    private static RemoteHost Host(JsonElement data)
    {
        var ip = Text(data, "ip");
        if (data.TryGetProperty("ip_addresses", out var addresses) && addresses.ValueKind == JsonValueKind.Array)
            foreach (var entry in addresses.EnumerateArray())
                if (Text(entry, "type") == "primary-ip" || ip == null) ip = Text(entry, "address") ?? Text(entry, "ip") ?? ip;
        if (!IPAddress.TryParse(ip, out _)) ip = null;
        var state = Text(data, "state")?.ToLowerInvariant(); var status = Text(data, "status")?.ToLowerInvariant();
        return new(Text(data, "id") ?? throw new InvalidOperationException("Cherry не вернул ID хоста."), ip,
            ip != null && status is not ("deploying" or "pending" or "provisioning" or "installing" or "rebooting" or "failed") && (state is "active" or "running" or "powered-on" || status is "active" or "running"), RentalRecord.FromCherry(data, DateTimeOffset.UtcNow));
    }
    public async Task<RemoteHost?> FindHost(string marker, CancellationToken ct)
    {
        var root = await Request(HttpMethod.Get, $"projects/{Required("Cherry:ProjectId")}/servers?fields=id,hostname,state,status,ip,ip_addresses&limit=0", null, ct);
        var matches = Items(root, "servers").Where(s => Text(s, "hostname") == marker).ToList();
        if (matches.Count > 1) throw new InvalidOperationException("Найдено несколько общих хостов. Автоматический выбор запрещён.");
        return matches.Count == 1 ? Host(matches[0]) : null;
    }
    public async Task<List<RentalLocation>> Locations(CancellationToken ct)
    {
        var regions=Items(await Request(HttpMethod.Get,"regions?limit=0",null,ct),"regions");
        var countries="AL AD AT BY BE BA BG HR CY CZ DK EE FI FR DE GR HU IS IE IT XK LV LI LT LU MT MD MC ME NL MK NO PL PT RO RU SM RS SK SI ES SE CH UA GB VA".Split(' ').ToHashSet(StringComparer.OrdinalIgnoreCase);
        var european=regions.Where(r=>countries.Contains(Text(r,"region_iso_2")??Text(r,"country_iso_2")??""));
        var tasks=european.Select(async r=>
        {
            var slug=Text(r,"slug")!;var root=await Request(HttpMethod.Get,$"teams/{Required("Cherry:TeamId")}/plans/amd-ryzen-9950x/prebuilts?region={Uri.EscapeDataString(slug)}&fields=prebuilt,stock,specs,pricing",null,ct);
            var prebuilts=Items(root,"prebuilts").ToList();
            var available=prebuilts.Where(p=>p.TryGetProperty("stock_qty",out var q)&&q.GetInt32()>0 && p.TryGetProperty("pricing",out var pp)&&pp.EnumerateArray().Any(x=>Text(x,"unit")?.ToLowerInvariant()=="hourly")).ToList();
            var stock=available.Sum(p=>p.TryGetProperty("stock_qty",out var qty)?qty.GetInt32():0);
            var choices=(available.Count>0?available:prebuilts).SelectMany(p=>p.TryGetProperty("pricing",out var pricing)&&pricing.ValueKind==JsonValueKind.Array ? pricing.EnumerateArray().Where(x=>Text(x,"unit")?.ToLowerInvariant()=="hourly").Select(x=>(Prebuilt:p,Price:x)).ToArray():[]).OrderBy(x=>x.Price.GetProperty("price").GetDecimal()).ToList();
            if(choices.Count==0)return new RentalLocation(slug,Text(r,"location")??Text(r,"name")??slug,stock,null,null);
            var best=choices[0];var specs=best.Prebuilt.GetProperty("specs");
            var cpu=specs.GetProperty("cpus");var memory=specs.GetProperty("memory");
            var storage=specs.TryGetProperty("storage",out var disks)?string.Join(", ",disks.EnumerateArray().Select(d=>Text(d,"count")+" x "+Text(d,"name"))):null;
            return new RentalLocation(slug,Text(r,"location")??Text(r,"name")??slug,stock,best.Price.GetProperty("price").GetDecimal(),Text(best.Price,"currency"),best.Prebuilt.GetProperty("id").GetInt32(),cpu.TryGetProperty("cores",out var cores)?cores.GetInt32():null,cpu.TryGetProperty("threads",out var threads)?threads.GetInt32():null,Text(memory,"total")+" "+Text(memory,"unit"),storage,specs.TryGetProperty("nics",out var nics)?Text(nics,"name"):null,specs.TryGetProperty("bandwidth",out var bandwidth)?Text(bandwidth,"name"):null);
        });
        return (await Task.WhenAll(tasks)).OrderBy(r=>r.Name).ToList();
    }
    public async Task<RemoteHost> Rent(string marker, CancellationToken ct)
    {
        if (config.GetValue<bool>("Cherry:DisableRent")) throw new InvalidOperationException("Новая аренда отключена владельцем: сначала подключите существующий хост.");
        var orderSent = false;
        try
        {
            var publicKey = SshKeyMaterial.PublicKey(new SshKeyVault(store).Snapshot(Required("Ssh:KeyPath")));
            var keys = Items(await Request(HttpMethod.Get, "ssh-keys?limit=0", null, ct), "ssh_keys");
            var key = keys.FirstOrDefault(k => SshKeyMaterial.Identity(Text(k, "key") ?? "") == publicKey);
            int keyId;
            if (key.ValueKind == JsonValueKind.Undefined)
            {
                var registered = await Request(HttpMethod.Post, "ssh-keys", new { label = "Squad Host Manager", key = publicKey }, ct);
                keyId = registered!.Value.GetProperty("id").GetInt32();
            }
            else keyId = key.GetProperty("id").GetInt32();
            var confirmed = await Request(HttpMethod.Get, $"ssh-keys/{keyId}", null, ct);
            if (confirmed == null || SshKeyMaterial.Identity(Text(confirmed.Value, "key") ?? "") != publicKey)
                throw new InvalidOperationException("Cherry вернул другой SSH-ключ. Аренда заблокирована.");
            store.Save("ssh-order-" + marker, new { KeyId = keyId, PublicKey = publicKey });
            var team = Required("Cherry:TeamId"); var plan = "amd-ryzen-9950x";
            var region = store.Load<string>("rental-region") ?? config["Cherry:Region"] ?? "DE-Frankfurt";
            var prebuiltRoot = await Request(HttpMethod.Get, $"teams/{team}/plans/{plan}/prebuilts?region={Uri.EscapeDataString(region)}&fields=prebuilt,stock,specs,pricing", null, ct);
            var available = Items(prebuiltRoot, "prebuilts").Where(p => p.TryGetProperty("stock_qty", out var qty) && qty.GetInt32() > 0)
                .SelectMany(p => p.GetProperty("pricing").EnumerateArray().Where(x => Text(x, "unit")?.ToLowerInvariant() == "hourly")
                    .Select(x => (Price: x.GetProperty("price").GetDecimal(), Id: p.GetProperty("id").GetInt32())))
                .OrderBy(x => x.Price).ToList();
            if (available.Count == 0) throw new InvalidOperationException("В выбранном регионе нет свободной почасовой конфигурации.");
            if(store.Load<RentHostRequest>("rental-quote-"+marker) is {} quote)
            {
                var matching=available.Where(a=>a.Id==quote.PrebuiltId && a.Price==quote.HourlyPrice).ToList();
                var sameCurrency=Items(prebuiltRoot,"prebuilts").Where(p=>p.GetProperty("id").GetInt32()==quote.PrebuiltId).SelectMany(p=>p.GetProperty("pricing").EnumerateArray()).Any(p=>Text(p,"unit")?.ToLowerInvariant()=="hourly" && Text(p,"currency")==quote.Currency);
                if(region!=quote.Region || matching.Count==0 || !sameCurrency)throw new InvalidOperationException("Подтверждённая конфигурация или цена изменилась. Платный заказ не отправлен.");
                available=matching;
            }
            orderSent = true;
            var created = await Request(HttpMethod.Post, $"projects/{Required("Cherry:ProjectId")}/servers?fields=id,hostname,state,status,ip,ip_addresses", new
            {
                plan,
                prebuilt_id = available[0].Id,
                region,
                image = "debian_13_64bit",
                cycle = "hourly",
                hostname = marker,
                ssh_keys = new[] { keyId },
                spot_market = false
            }, ct, rental: true);
            return Host(created!.Value);
        }
        catch (Exception e) when (!orderSent && e is not OperationCanceledException) { throw new RentalRejectedException(e.Message); }
    }
    public async Task<RemoteHost?> GetHost(string id, CancellationToken ct)
    {
        var root = await Request(HttpMethod.Get, $"servers/{id}", null, ct);
        return root is { } r ? Host(r) : null;
    }
    public async Task Delete(string hostId, CancellationToken ct)
    {
        if (config.GetValue<bool>("Cherry:DisableRelease")) throw new InvalidOperationException("Прекращение аренды отключено владельцем.");
        _ = await Request(HttpMethod.Delete, $"servers/{hostId}", null, ct);
    }
    public Task Prepare(string hostId, string ip, CancellationToken ct) => docker.Prepare(hostId, ip, ct);
    public Task Prepare(string hostId, string ip, Action<string> progress, CancellationToken ct) => docker.Prepare(hostId, ip, ct, progress);
    public Task<GameStatus?> PreparationStatus(string hostId,string ip,CancellationToken ct) => docker.PreparationStatus(hostId,ip,ct);
    public Task<List<string>> CpuSets(string hostId,string ip,CancellationToken ct) => docker.CpuSets(hostId,ip,ct);
    public Task<List<RemoteInstance>> Inventory(string hostId, string ip, CancellationToken ct) => docker.Inventory(hostId, ip, ct);
    public Task<HashSet<int>> BoundPorts(string hostId, string ip, CancellationToken ct) => docker.BoundPorts(hostId, ip, ct);
    public Task Start(string hostId, string ip, Instance instance, CancellationToken ct) => docker.Start(hostId, ip, instance, ct);
    public Task Stop(string hostId, string ip, string instanceId, CancellationToken ct) => docker.Stop(hostId, ip, instanceId, ct);
    public Task<string> Logs(string hostId, string ip, string instanceId, CancellationToken ct) => docker.Logs(hostId, ip, instanceId, ct);
    public Task<string> Command(string hostId, string ip, Instance instance, string command, CancellationToken ct) => docker.Command(hostId, ip, instance, command, ct);
    public Task<string> Config(string hostId, string ip, string instanceId, SquadHostManager.Core.ConfigRequest request, CancellationToken ct) => docker.Config(hostId, ip, instanceId, request, ct);
    public Task<Stream> ArchiveStream(string hostId,string ip,string instanceId,CancellationToken ct) => docker.ArchiveStream(hostId,ip,instanceId,ct);
    public Task<Stream> BackupArchiveStream(string hostId,string ip,string instanceId,CancellationToken ct)=>docker.BackupArchiveStream(hostId,ip,instanceId,ct);
    public Task<MetricSample> Metrics(string hostId,string ip,Instance instance,CancellationToken ct)=>docker.Metrics(hostId,ip,instance,ct);
    public Task<LogBatch> MatchLogs(string hostId,string ip,Instance instance,CancellationToken ct)=>docker.MatchLogs(hostId,ip,instance,ct);
    public Task RestoreFiles(string hostId,string ip,string instanceId,string localArchive,CancellationToken ct)=>docker.RestoreFiles(hostId,ip,instanceId,localArchive,ct);
    public Task<byte[]> Archive(string hostId, string ip, string instanceId, CancellationToken ct) => docker.Archive(hostId, ip, instanceId, ct);
    public Task<List<SquadHostManager.Core.ServerFile>> Files(string hostId, string ip, string instanceId, CancellationToken ct) => docker.Files(hostId, ip, instanceId, ct);
    public Task<byte[]> Download(string hostId, string ip, string instanceId, string path, CancellationToken ct) => docker.Download(hostId, ip, instanceId, path, ct);
    public Task<GameStatus?> Probe(string hostId, string ip, Instance instance, CancellationToken ct) => docker.Probe(hostId, ip, instance, ct);
}
