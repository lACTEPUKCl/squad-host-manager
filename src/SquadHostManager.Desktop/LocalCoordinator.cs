using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace SquadHostManager.Desktop;
public static class LocalCoordinator
{
    public const string Endpoint="http://127.0.0.1:18087/";
    private static readonly string Root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SquadHostManager","private-hosting");
    private static readonly string Settings=Path.Combine(Root,"account.dat");
    private static readonly SemaphoreSlim StartupLock=new(1,1);
    public record Account(string Token,string TeamId,string ProjectId,string Password);
    public static bool Configured=>File.Exists(Settings);
    public static Account Read()=>JsonSerializer.Deserialize<Account>(ProtectedData.Unprotect(File.ReadAllBytes(Settings),null,DataProtectionScope.CurrentUser))!;
    public static void Save(Account account)
    {
        Directory.CreateDirectory(Root);
        var key=Path.Combine(Root,"ssh-key");
        if(!File.Exists(key)){using var rsa=RSA.Create(3072);File.WriteAllText(key,rsa.ExportRSAPrivateKeyPem());}
        var security=new FileSecurity();security.SetAccessRuleProtection(true,false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.FullControl,AccessControlType.Allow));
        new FileInfo(key).SetAccessControl(security);
        var temporary=Settings+".tmp-"+Guid.NewGuid().ToString("N");
        try{File.WriteAllBytes(temporary,ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(account),null,DataProtectionScope.CurrentUser));File.Move(temporary,Settings,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static async Task EnsureStarted()
    {
        await StartupLock.WaitAsync();
        try{await Start();}finally{StartupLock.Release();}
    }
    private static async Task Start()
    {
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(2)};
        var account=Read();
        async Task<bool> Running()
        {
            try
            {
                using var check=await http.GetAsync(Endpoint+"healthz");
                if(!check.IsSuccessStatusCode)throw new InvalidOperationException(Localization.T("localPortOccupied"));
                using var data=JsonDocument.Parse(await check.Content.ReadAsStringAsync());
                if(!data.RootElement.TryGetProperty("application",out var application)||application.GetString()!="squad-host-manager")throw new InvalidOperationException(Localization.T("localPortOccupied"));
                return true;
            }
            catch(HttpRequestException){return false;}catch(TaskCanceledException){return false;}
            catch(JsonException){throw new InvalidOperationException(Localization.T("localPortOccupied"));}
        }
        if(await Running())return;
        using var lease=await AcquireStartupLease();
        if(await Running())return;
        var folder=Path.Combine(Root,"bin");Directory.CreateDirectory(folder);
        var exe=Path.Combine(folder,"SquadHostManager.Service.exe");
        using(var resource=typeof(LocalCoordinator).Assembly.GetManifestResourceStream("SquadHostManager.Coordinator"))
        {
            if(resource==null)throw new InvalidOperationException(Localization.T("localCoordinatorMissing"));
            using var target=File.Create(exe);await resource.CopyToAsync(target);
        }
        var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Root};
        start.Environment["ASPNETCORE_URLS"]=Endpoint.TrimEnd('/');
        start.Environment["DataDir"]=Path.Combine(Root,"data");
        start.Environment["Admin__Password"]=account.Password;
        start.Environment["Cherry__Token"]=account.Token;
        start.Environment["Cherry__TeamId"]=account.TeamId;
        start.Environment["Cherry__ProjectId"]=account.ProjectId;
        start.Environment["Cherry__Region"]="DE-Frankfurt";
        start.Environment["Cherry__Plan"]="amd-ryzen-9950x";
        start.Environment["Hosting__PersonalMode"]="true";
        start.Environment["Ssh__KeyPath"]=Path.Combine(Root,"ssh-key");
        using var process=Process.Start(start)??throw new InvalidOperationException(Localization.T("localCoordinatorMissing"));
        for(var attempt=0;attempt<30;attempt++)
        {
            await Task.Delay(500);
            if(process.HasExited)throw new InvalidOperationException(Localization.T("localCoordinatorMissing"));
            if(await Running())return;
        }
        throw new TimeoutException(Localization.T("localCoordinatorMissing"));
    }
    private static async Task<FileStream> AcquireStartupLease()
    {
        Directory.CreateDirectory(Root);
        for(var attempt=0;attempt<100;attempt++)
        {
            try{return new FileStream(Path.Combine(Root,"startup.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException){await Task.Delay(200);}
        }
        throw new TimeoutException(Localization.T("localCoordinatorMissing"));
    }
}
