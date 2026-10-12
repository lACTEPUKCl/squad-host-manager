using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public record Release(Version Version,Uri Exe,Uri Manifest,Uri Signature,string Notes="");
public static class Updates
{
    private const string Asset = "SquadHostManager.exe";
    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5),MaxResponseContentBufferSize=262144 }; http.DefaultRequestHeaders.UserAgent.ParseAdd("SquadHostManager/0.1"); return http;
    }
    public static async Task<Release?> Check()
    {
        using var http = Client(); http.Timeout = TimeSpan.FromSeconds(20); using var response = await http.GetAsync($"https://api.github.com/repos/{Product.Repository}/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new HttpRequestException("No published release.", null, response.StatusCode);
        response.EnsureSuccessStatusCode(); var root = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (!Version.TryParse(root.GetProperty("tag_name").GetString()!.TrimStart('v'), out var version)) throw new InvalidOperationException("Некорректная версия релиза.");
        var current = Assembly.GetExecutingAssembly().GetName().Version!;
        if (version <= new Version(current.Major, current.Minor, current.Build)) return null;
        Uri Find(string name)
        {
            var assets=root.GetProperty("assets").EnumerateArray().Where(a=>a.GetProperty("name").GetString()==name).ToList();if(assets.Count!=1)throw new InvalidOperationException(Localization.T("updateSignatureMissing"));var asset=assets[0];
            var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith($"/{Product.Repository}/releases/download/", StringComparison.Ordinal)) throw new InvalidOperationException("Обновление должно принадлежать официальному репозиторию.");
            return uri;
        }
        var body=root.TryGetProperty("body",out var description)?description.GetString()??"":"";
        var heading="## "+Localization.Language;var section=body.Split(heading+"\n",StringSplitOptions.None);
        var notes=section.Length>1?section[1].Split("\n## ")[0].Trim():body;
        return new(version, Find(Asset), Find(Asset+".manifest.json"),Find(Asset+".manifest.sig"),notes);
    }
    public static async Task Install(Release release,IProgress<long>? progress=null)
    {
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь приложения.");
        if (Path.GetFileName(target).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Обновление доступно в опубликованной EXE-сборке.");
        var stage = Path.Combine(Path.GetDirectoryName(target)!, ".shm-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        var stagedExe = Path.Combine(stage, Asset); using var http = Client();
        try
        {
            var manifest=await http.GetByteArrayAsync(release.Manifest);var signature=await http.GetStringAsync(release.Signature);
            using(var source=await http.GetStreamAsync(release.Exe))using(var dest=File.Create(stagedExe))
            {
                var buffer=new byte[65536];long size=0;int read;
                while((read=await source.ReadAsync(buffer))>0){size+=read;if(size>1024L*1024*1024)throw new InvalidOperationException("Обновление слишком большое.");await dest.WriteAsync(buffer.AsMemory(0,read));progress?.Report(size);}
            }
            using var file=File.OpenRead(stagedExe);var actual=Convert.ToHexString(await SHA256.HashDataAsync(file));
            UpdateTrust.Verify(manifest,signature,release.Version.ToString(),actual,file.Length);file.Close();
            var profileArgs=Environment.GetCommandLineArgs();var profileIndex=Array.IndexOf(profileArgs,"--profile");var profile=profileIndex>=0 && profileIndex+1<profileArgs.Length?profileArgs[profileIndex+1]:"";
            var scriptPath = Path.Combine(stage, "install.ps1");
            // Paths are embedded as base64 data, never interpolated as executable syntax.
            static string Enc(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                function Decode($v) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($v)) }
                $taskTarget = Decode '{{Enc(target)}}'
                $taskSource = Decode '{{Enc(stagedExe)}}'
                $taskBackup = $taskTarget + '.previous'
                $taskProfile = Decode '{{Enc(profile)}}'
                Wait-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
                try {
                  Copy-Item -LiteralPath $taskTarget -Destination $taskBackup -Force
                  Copy-Item -LiteralPath $taskSource -Destination $taskTarget -Force
                  if ($taskProfile) { Start-Process -FilePath $taskTarget -ArgumentList @("--profile",$taskProfile) -WorkingDirectory (Split-Path -LiteralPath $taskTarget) -WindowStyle Normal } else { Start-Process -FilePath $taskTarget -WorkingDirectory (Split-Path -LiteralPath $taskTarget) -WindowStyle Normal }
                } catch {
                  if (Test-Path -LiteralPath $taskBackup) { Copy-Item -LiteralPath $taskBackup -Destination $taskTarget -Force }
                  $_ | Out-File -LiteralPath ($taskTarget + '.update-error.txt')
                }
                """;
            await File.WriteAllTextAsync(scriptPath, script);
            Process.Start(new ProcessStartInfo("powershell.exe") { Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"", UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch { if (File.Exists(stagedExe)) File.Delete(stagedExe); throw; }
    }
}
