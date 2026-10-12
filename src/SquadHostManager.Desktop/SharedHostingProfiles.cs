using SquadHostManager.Core;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
namespace SquadHostManager.Desktop;
public static class SharedHostingProfiles
{
    private static readonly string FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SquadHostManager","shared-hosting.dat");
    public static IReadOnlyList<SharedHostingProfile> Read()
    {
        if(!File.Exists(FileName))return [];
        try{return JsonSerializer.Deserialize<List<SharedHostingProfile>>(ProtectedData.Unprotect(File.ReadAllBytes(FileName),null,DataProtectionScope.CurrentUser))??[];}
        catch(Exception error) when(error is IOException or CryptographicException or JsonException){return [];}
    }
    public static void Remove(string endpoint) => Write(Read().Where(p=>p.Endpoint!=endpoint).ToList());
    public static void Save(SharedHostingProfile profile) => Write(Read().Where(p=>p.Endpoint!=profile.Endpoint).Append(profile).ToList());
    private static void Write(List<SharedHostingProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);var temp=FileName+"."+Guid.NewGuid().ToString("N");
        try{File.WriteAllBytes(temp,ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(profiles),null,DataProtectionScope.CurrentUser));if(File.Exists(FileName))File.Copy(FileName,FileName+".previous",true);File.Move(temp,FileName,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
