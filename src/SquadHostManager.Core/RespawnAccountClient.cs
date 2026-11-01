using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace SquadHostManager.Core;

public sealed class RespawnAccountException(string key):Exception(key)
{public string MessageKey{get;}=key;}

public sealed class RespawnAccountClient(HttpClient http)
{
    public async Task<string> Inspect(string token,CancellationToken ct=default)
    {
        token=token.Trim();
        if(!Regex.IsMatch(token,"^rsp_[a-fA-F0-9]{64}$"))throw new RespawnAccountException("respawnKeyInvalid");
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://respawnhost.com/api/v1/servers?limit=1");request.Headers.Add("X-Api-Key",token);
        using var response=await http.SendAsync(request,ct);
        if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)throw new RespawnAccountException("respawnKeyInvalid");
        if(!response.IsSuccessStatusCode)throw new RespawnAccountException("respawnAccountNetworkError");
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if(!doc.RootElement.TryGetProperty("data",out var data) || data.ValueKind!=JsonValueKind.Array)throw new RespawnAccountException("respawnAccountNetworkError");
        return token;
    }
}
