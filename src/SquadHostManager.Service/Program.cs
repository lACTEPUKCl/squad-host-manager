using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using SquadHostManager.Core;
using SquadHostManager.Service;

if (args.Length == 2 && args[0] == "--check-removal")
{
    try
    {
        using var store = new Store(args[1]);
        var state = store.Load<PoolState>("pool") ?? new();
        Environment.ExitCode = RemovalSafety.CanRemove(state) ? 0 : 43;
    }
    catch { Environment.ExitCode = 44; }
    return;
}
var builder = WebApplication.CreateBuilder(args);
var dataDir = builder.Configuration["DataDir"] ?? Path.Combine(AppContext.BaseDirectory, "data");
builder.Services.AddSingleton(new Store(dataDir));
builder.Services.AddSingleton<Accounts>();
builder.Services.AddSingleton<AccountBalance>();
builder.Services.AddSingleton<RentalCatalog>();
builder.Services.AddSingleton<IInfrastructure, CherryInfrastructure>();
builder.Services.AddSingleton<PoolCoordinator>();
builder.Services.AddHostedService<PoolWorker>();
builder.Services.AddHostedService<AutomationWorker>();
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 8192);
builder.Services.AddAuthentication("session").AddScheme<AuthenticationSchemeOptions, SessionAuthentication>("session", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("user", c =>
    {
        var reading=HttpMethods.IsGet(c.Request.Method);
        return RateLimitPartition.GetTokenBucketLimiter((c.User.FindFirstValue(ClaimTypes.NameIdentifier)??"unknown")+(reading?":read":":write"),
            _=>new TokenBucketRateLimiterOptions{TokenLimit=reading?120:60,TokensPerPeriod=reading?120:30,ReplenishmentPeriod=TimeSpan.FromMinutes(1),QueueLimit=0,AutoReplenishment=true});
    });
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 32 * 1024);
var app = builder.Build();
var accounts = app.Services.GetRequiredService<Accounts>();
var adminPassword = builder.Configuration["Admin:Password"];
if (string.IsNullOrEmpty(adminPassword))
{
    var passwordFile = Path.Combine(dataDir, "initial-admin-password.txt");
    if (!File.Exists(passwordFile)) File.WriteAllText(passwordFile, Store.Secret());
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(passwordFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    adminPassword = File.ReadAllText(passwordFile).Trim();
}
accounts.Bootstrap(adminPassword);
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    try { await next(); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (TimeoutException e) { ctx.Response.StatusCode = 504; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (UnauthorizedAccessException e) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (ArgumentException e) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (InvalidOperationException e) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        app.Logger.LogError("Request failed ({Type})", e.GetType().Name);
        ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "Операция не выполнена. Проверьте состояние перед повтором." });
    }
});
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", features=true, application="squad-host-manager", version = typeof(Store).Assembly.GetCustomAttributes(false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion }));
app.MapPost("/api/register", (RegisterRequest r) => accounts.Register(r)).RequireRateLimiting("login");
app.MapPost("/api/login", (LoginRequest r) => accounts.Login(r)).RequireRateLimiting("login");
app.MapPost("/api/key-login", (KeyLoginRequest r) => accounts.KeyLogin(r)).RequireRateLimiting("login");
var api = app.MapGroup("/api").RequireAuthorization().RequireRateLimiting("user");
static User Current(HttpContext c) => new(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!, c.User.Identity!.Name!, c.User.IsInRole("admin"));
api.MapPost("/logout", (HttpContext c) => { accounts.Logout(c.Request.Headers.Authorization.ToString()[7..]); return Results.NoContent(); });
api.MapGet("/pool", async (HttpContext c, PoolCoordinator pool) => { if (c.Request.Query["refresh"] == "true") { try { await pool.Reconcile(c.RequestAborted); } catch (Exception) when (!c.RequestAborted.IsCancellationRequested) { } } return pool.Snapshot(Current(c)); });
api.MapPost("/rent", async (RentHostRequest r, PoolCoordinator pool,HttpContext c) => Results.Accepted(value:await pool.RentHost(Current(c),r,c.RequestAborted)));
api.MapGet("/locations", (RentalCatalog catalog, HttpContext c) => catalog.Read(c.RequestAborted));
api.MapGet("/location", (Store store,IConfiguration config) => new RentalLocationRequest(store.Load<string>("rental-region")??config["Cherry:Region"]??"DE-Frankfurt"));
api.MapPost("/location", async (RentalLocationRequest r, PoolCoordinator pool, HttpContext c) => { await pool.SetRegion(Current(c),r.Region,c.RequestAborted); return Results.NoContent(); });
api.MapGet("/admin/statistics", (HttpContext c, PoolCoordinator pool, DateTimeOffset? from) => pool.Statistics(Current(c),from));
api.MapGet("/admin/users", (HttpContext c) => accounts.Users(Current(c)));
api.MapPost("/admin/cherry-key", (HttpContext c, PersonalAccountKeyRequest request) =>
{
    if(!Current(c).IsAdmin || !builder.Configuration.GetValue<bool>("Hosting:PersonalMode") || c.Connection.RemoteIpAddress is not {} address || !System.Net.IPAddress.IsLoopback(address))throw new UnauthorizedAccessException("Недоступно для этого подключения.");
    if(string.IsNullOrWhiteSpace(request.Token) || request.Token.Length>4096 || request.TeamId!=builder.Configuration["Cherry:TeamId"] || request.ProjectId!=builder.Configuration["Cherry:ProjectId"])throw new ArgumentException("Аккаунт и проект должны совпадать с настройкой хостинга.");
    builder.Configuration["Cherry:Token"]=request.Token;
    return Results.NoContent();
});
api.MapPost("/admin/users", (HttpContext c, CreateUserRequest r) => { if(builder.Configuration.GetValue<bool>("Hosting:PersonalMode"))throw new InvalidOperationException("Личный хостинг не принимает подключения с других компьютеров.");return accounts.CreateUser(Current(c), r.Name); });
api.MapPost("/admin/users/{id}/key", (HttpContext c, string id) => accounts.RotateKey(Current(c), id));
api.MapPost("/admin/users/{id}/revoke", (HttpContext c, string id) => { accounts.RevokeUser(Current(c), id); return Results.NoContent(); });
api.MapPost("/instances", (HttpContext c, PoolCoordinator pool, StartRequest r) => Results.Accepted(value: pool.Start(Current(c), r)));
api.MapPost("/stop", (HttpContext c, PoolCoordinator pool, StopRequest r) => Results.Accepted(value: pool.Stop(Current(c), r)));
api.MapPost("/instances/{id}/settings", (string id, ServerSettingsRequest r, HttpContext c, PoolCoordinator pool) => { pool.Settings(Current(c), id, r); return Results.NoContent(); });
api.MapPost("/instances/{id}/restart", (string id, RestartRequest r, HttpContext c, PoolCoordinator pool) => Results.Accepted(value: pool.Restart(Current(c), id, r)));
api.MapGet("/instances/{id}/logs", async (string id, HttpContext c, PoolCoordinator pool) => new { output = await pool.Access(Current(c), id, null, c.RequestAborted) });
api.MapPost("/instances/{id}/rcon", async (string id, CommandRequest r, HttpContext c, PoolCoordinator pool) => new { output = await pool.Access(Current(c), id, r.Command, c.RequestAborted) });
api.MapPost("/instances/{id}/config", async (string id, ConfigRequest r, HttpContext c, PoolCoordinator pool) => new { output = await pool.Config(Current(c), id, r, c.RequestAborted) });
api.MapGet("/instances/{id}/files", async (string id, HttpContext c, PoolCoordinator pool) => await pool.Files(Current(c), id, c.RequestAborted));
api.MapGet("/instances/{id}/file", async (string id, string path, HttpContext c, PoolCoordinator pool) => Results.File(await pool.Download(Current(c), id, path, c.RequestAborted), "application/octet-stream", Path.GetFileName(path)));
api.MapGet("/instances/{id}/archive", async (string id, HttpContext c, PoolCoordinator pool) => Results.File(await pool.ArchiveStream(Current(c), id, c.RequestAborted), "application/gzip", "squad-archive.tar.gz"));
api.MapPost("/admin/invites", (InviteRequest r, HttpContext c) =>
{
    if (!Current(c).IsAdmin) throw new UnauthorizedAccessException("Только администратор создаёт приглашения.");
    return new { invite = accounts.Invite(r.Uses) };
});
api.MapGet("/features",(string? instanceId,HttpContext c,PoolCoordinator pool)=>pool.Features(Current(c),instanceId));
api.MapGet("/balance",async(HttpContext c,AccountBalance balance)=>{Current(c);return await balance.Read(c.RequestAborted);});
api.MapGet("/server-defaults",(HttpContext c,PoolCoordinator pool)=>pool.Defaults(Current(c)));
api.MapGet("/server-defaults/versions",(string file,HttpContext c,PoolCoordinator pool)=>pool.CommonConfigHistory(Current(c),file));
api.MapGet("/server-defaults/stock",(string file,HttpContext c,PoolCoordinator pool)=>new{output=pool.StandardConfig(Current(c),file)});
api.MapPost("/server-defaults/config",async(HttpContext c,PoolCoordinator pool,ConfigRequest request)=>await pool.SaveCommonConfig(Current(c),request,c.RequestAborted));
api.MapGet("/server-tools",(string? instanceId,HttpContext c,PoolCoordinator pool)=>pool.Tools(Current(c),instanceId));
api.MapPost("/admin/prepare",(RestartRequest r,HttpContext c,PoolCoordinator pool)=>Results.Accepted(value:pool.RetryHostPreparation(Current(c),r)));
api.MapGet("/matches/{id}/archive",(string id,HttpContext c,PoolCoordinator pool)=>Results.File(pool.DownloadMatchArchive(Current(c),id),"application/gzip","squad-logs.tar.gz"));
api.MapDelete("/matches/{id}",(string id,HttpContext c,PoolCoordinator pool)=>{pool.DeleteMatch(Current(c),id);return Results.NoContent();});
api.MapPost("/instances/{id}/clone",async(string id,CopyRequest r,HttpContext c,PoolCoordinator pool)=>Results.Accepted(value:await pool.Clone(Current(c),id,r,c.RequestAborted)));
api.MapPost("/instances/{id}/backups",async(string id,BackupRequest r,HttpContext c,PoolCoordinator pool)=>await pool.CreateBackup(Current(c),id,r,c.RequestAborted));
api.MapPost("/backups/import",async(HttpContext c,PoolCoordinator pool)=>
{
    var size=c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();if(size is {IsReadOnly:false})size.MaxRequestBodySize=BackupFormat.MaxBytes;
    return await pool.ImportBackup(Current(c),c.Request.Body,c.RequestAborted);
});
api.MapGet("/backups/{id}",(string id,HttpContext c,PoolCoordinator pool)=>Results.File(pool.ExportBackup(Current(c),id),"application/octet-stream","squad.shmbackup"));
api.MapDelete("/backups/{id}",(string id,HttpContext c,PoolCoordinator pool)=>{pool.DeleteBackup(Current(c),id);return Results.NoContent();});
api.MapPost("/backups/{id}/restore",(string id,CopyRequest r,HttpContext c,PoolCoordinator pool)=>Results.Accepted(value:pool.RestoreBackup(Current(c),id,r)));
api.MapPost("/discord",(DiscordSettings r,HttpContext c,PoolCoordinator pool)=>{pool.SetDiscord(Current(c),r);return Results.NoContent();});
api.MapPost("/discord/test",async(HttpContext c,PoolCoordinator pool)=>{await pool.TestDiscord(Current(c),c.RequestAborted);return Results.NoContent();});
api.MapPost("/templates",async(TemplateRequest r,HttpContext c,PoolCoordinator pool)=>await pool.SaveTemplate(Current(c),r,c.RequestAborted));
api.MapPost("/templates/{id}/start",(string id,TemplateStart r,HttpContext c,PoolCoordinator pool)=>pool.StartTemplate(Current(c),id,r));
api.MapDelete("/templates/{id}",(string id,HttpContext c,PoolCoordinator pool)=>{pool.DeleteTemplate(Current(c),id);return Results.NoContent();});
api.MapPost("/budget",(BudgetPolicy r,HttpContext c,PoolCoordinator pool)=>{pool.SetBudget(Current(c),r);return Results.NoContent();});
api.MapPost("/notices/read",(HttpContext c,PoolCoordinator pool)=>{pool.ReadNotices(Current(c));return Results.NoContent();});
api.MapGet("/instances/{id}/versions",(string id,string file,HttpContext c,PoolCoordinator pool)=>pool.Versions(Current(c),id,file));
api.MapPost("/instances/{id}/versions/{version}/restore",async(string id,string version,HttpContext c,PoolCoordinator pool)=>new{output=await pool.RestoreVersion(Current(c),id,version,c.RequestAborted)});
api.MapGet("/instances/{id}/diagnosis",async(string id,HttpContext c,PoolCoordinator pool)=>await pool.Diagnose(Current(c),id,c.RequestAborted));
api.MapPost("/admin/users/{id}/policy",(string id,AccessPolicy r,HttpContext c)=>{accounts.SetPolicy(Current(c),id,r);return Results.NoContent();});
app.MapHub<PoolHub>("/events").RequireAuthorization();
api.MapPost("/admin/adopt", async (AdoptRequest r, HttpContext c, PoolCoordinator pool) => { await pool.Adopt(Current(c), r.HostId, c.RequestAborted); return Results.NoContent(); });
api.MapPost("/admin/recover", async (RecoveryRequest r, HttpContext c, PoolCoordinator pool) => { await pool.Recover(Current(c), r.ConfirmNoPendingCherryOrder, c.RequestAborted); return Results.NoContent(); });
app.Run();

namespace SquadHostManager.Service
{
    public sealed class SessionAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, Accounts accounts)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var value = Request.Headers.Authorization.ToString();
            var token = value.StartsWith("Bearer ", StringComparison.Ordinal) ? value[7..] : "";
            if (Request.Path.StartsWithSegments("/events") && string.IsNullOrEmpty(token)) token = Request.Query["access_token"].ToString();
            var user = token.Length == 64 ? accounts.Authenticate(token) : null;
            if (user == null) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.Name) };
            if (user.IsAdmin) claims.Add(new(ClaimTypes.Role, "admin"));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
    public sealed class PoolHub : Hub
    {
        public override async Task OnConnectedAsync() { await Clients.Caller.SendAsync("changed"); await base.OnConnectedAsync(); }
    }
    public sealed class PoolWorker(PoolCoordinator pool, IHubContext<PoolHub> hub, ILogger<PoolWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            pool.Changed += () => _ = Broadcast(ct);
            var tick = 0;
            while (!ct.IsCancellationRequested)
            {
                try { await pool.ProcessNext(ct); if (++tick % 15 == 0) await pool.Reconcile(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception e) { logger.LogWarning("Pool reconciliation failed ({Type}); deletion remains blocked", e.GetType().Name); }
                await Task.Delay(1000, ct);
            }
        }
        private async Task Broadcast(CancellationToken ct)
        {
            try { await hub.Clients.All.SendAsync("changed", cancellationToken: ct); } catch (Exception e) when (e is not OutOfMemoryException) { logger.LogDebug("Event delivery failed"); }
        }
    }
}

public record PersonalAccountKeyRequest(string Token,string TeamId,string ProjectId);
