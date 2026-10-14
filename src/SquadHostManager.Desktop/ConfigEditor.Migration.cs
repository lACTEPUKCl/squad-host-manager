using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public sealed partial class ConfigEditor
{
    public static void MigrateLegacyDrafts(string context,IEnumerable<string> ownedIds)
    {
        string Folder(string scope)=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SquadHostManager","config-drafts",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context+"|"+scope))));
        var target=Folder("defaults");
        foreach(var file in Files)
        {
            var destination=Path.Combine(target,file+".dat");
            foreach(var source in ownedIds.Select(id=>Path.Combine(Folder(id),file+".dat")).Where(File.Exists).OrderBy(p=>File.GetLastWriteTimeUtc(p)))
            {
                if(File.Exists(destination) && File.GetLastWriteTimeUtc(destination)>=File.GetLastWriteTimeUtc(source))continue;
                try
                {
                    var draft=JsonSerializer.Deserialize<Draft>(ProtectedData.Unprotect(File.ReadAllBytes(source),null,DataProtectionScope.CurrentUser));
                    if(draft?.Pending!=true)continue;
                    if(file=="Server.cfg")draft=draft with{Text=StandardConfigs.SharedServer(draft.Text)};
                    Directory.CreateDirectory(target);var temp=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
                    try{File.WriteAllBytes(temp,ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(draft),null,DataProtectionScope.CurrentUser));File.Move(temp,destination,true);File.SetLastWriteTimeUtc(destination,File.GetLastWriteTimeUtc(source));}
                    finally{if(File.Exists(temp))File.Delete(temp);}
                }
                catch(IOException) { }
                catch(CryptographicException) { }
                catch(JsonException) { }
            }
        }
    }
}
