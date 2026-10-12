using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SquadHostManager.Desktop;
public sealed record CherryProject(string Id, string TeamId, string Name);
public sealed class CherryAccountException(string key) : Exception(key)
{
    public string MessageKey { get; } = key;
}
public sealed class CherryAccountClient(HttpClient http)
{
    public async Task<IReadOnlyList<CherryProject>> Inspect(string token, CancellationToken cancellation = default)
    {
        if(string.IsNullOrWhiteSpace(token)||token.Length>4096||token.Trim().Any(char.IsControl))throw new CherryAccountException("accountKeyRejected");
        async Task<JsonElement> Read(string path)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,new Uri(new Uri("https://api.cherryservers.com/v1/"),path));
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token.Trim());
            using var response=await http.SendAsync(request,cancellation);
            if(response.StatusCode==HttpStatusCode.Unauthorized)throw new CherryAccountException("accountKeyRejected");
            if(response.StatusCode==HttpStatusCode.Forbidden)throw new CherryAccountException("accountNoPermission");
            if(!response.IsSuccessStatusCode)throw new CherryAccountException("accountNetworkError");
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:cancellation);
        }
        try
        {
            var teams=await Read("teams");var projects=new List<CherryProject>();
            foreach(var team in teams.EnumerateArray())
            {
                var teamId=team.GetProperty("id").ToString();
                foreach(var project in (await Read($"teams/{Uri.EscapeDataString(teamId)}/projects")).EnumerateArray())
                    projects.Add(new(project.GetProperty("id").ToString(),teamId,$"{team.GetProperty("name").GetString()} / {project.GetProperty("name").GetString()}"));
            }
            return projects;
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new CherryAccountException("accountNetworkError"); }
    }
}
