using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SquadHostManager.Desktop;

public sealed record ServerDraft(string Name,string Password,string MaxPlayers,string Mods);
public static class ServerDraftFile
{
    public static string FileName(string endpoint,string userId)=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SquadHostManager","draft-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint+"|"+userId)))+".dat");
    public static void Save(string path,ServerDraft draft)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary=path+".tmp";
        File.WriteAllBytes(temporary,ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(draft),null,DataProtectionScope.CurrentUser));
        File.Move(temporary,path,true);
    }
    public static ServerDraft? Read(string path)
    {
        if(!File.Exists(path))return null;
        return JsonSerializer.Deserialize<ServerDraft>(ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser));
    }
}

public partial class MainWindow
{
    private string? draftFile;
    private bool draftPersistenceReady,restoringDraft;
    private readonly DispatcherTimer draftSaveTimer=new(){Interval=TimeSpan.FromMilliseconds(400)};
    private void InitializeDraftSaving()
    {
        draftSaveTimer.Tick+=(_,_)=>{draftSaveTimer.Stop();SaveServerDraft();};
        void Changed(object? sender,EventArgs args){if(!draftPersistenceReady || restoringDraft || Mine!=null)return;draftSaveTimer.Stop();draftSaveTimer.Start();}
        ServerName.TextChanged+=Changed;ServerPassword.PasswordChanged+=Changed;MaxPlayers.TextChanged+=Changed;
        Closed+=(_,_)=>{draftSaveTimer.Stop();SaveServerDraft();};
    }
    private void SaveServerDraft()
    {
        if(!draftPersistenceReady || restoringDraft || draftFile==null || session==null || Mine!=null)return;
        try
        {
            if(ServerName.Text=="My Squad Server" && ServerPassword.Password.Length==0 && MaxPlayers.Text=="100" && Mods.Text==defaultMods){ClearServerDraft();return;}
            ServerDraftFile.Save(draftFile,new(ServerName.Text,ServerPassword.Password,MaxPlayers.Text,Mods.Text));
        }
        catch(Exception error){Status.Text=L("draftSaveFailed");ErrorDetails.Text=error.Message;ErrorPanel.Visibility=System.Windows.Visibility.Visible;}
    }
    private void RestoreServerDraft()
    {
        draftFile=ServerDraftFile.FileName(BaseUri().ToString(),session!.UserId);restoringDraft=true;
        try
        {
            var saved=ServerDraftFile.Read(draftFile);if(saved==null)return;
            draft=true;selectedId=null;displayedInstance=null;ClearInstanceViews();
            ServerName.Text=saved.Name;ServerPassword.Password=saved.Password;MaxPlayers.Text=saved.MaxPlayers;Mods.Text=saved.Mods;
            UpdateModList();RenderPool();
        }
        catch(Exception error){Status.Text=L("draftRestoreFailed");ErrorDetails.Text=error.Message;ErrorPanel.Visibility=System.Windows.Visibility.Visible;}
        finally{restoringDraft=false;draftPersistenceReady=true;}
    }
    private void ClearServerDraft()
    {
        draftSaveTimer.Stop();try{if(draftFile!=null && File.Exists(draftFile))File.Delete(draftFile);}catch(Exception error){Status.Text=L("draftSaveFailed");ErrorDetails.Text=error.Message;ErrorPanel.Visibility=System.Windows.Visibility.Visible;}
    }
}
