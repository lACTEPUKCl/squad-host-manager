using System.Security.Cryptography;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using SquadHostManager.Core;

if(args.Length==3 && args[0]=="init")
{
    if(File.Exists(args[1]))throw new InvalidOperationException("Signing key already exists.");
    using var rsa=RSA.Create(3072);var secret=rsa.ExportPkcs8PrivateKey();
    try
    {
        File.WriteAllBytes(args[1],ProtectedData.Protect(secret,null,DataProtectionScope.CurrentUser));
        var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.FullControl,AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-18"),FileSystemRights.FullControl,AccessControlType.Allow));
        new FileInfo(args[1]).SetAccessControl(acl);
    }
    finally{CryptographicOperations.ZeroMemory(secret);}
    File.WriteAllText(args[2],Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));Console.WriteLine("Signing key protected with Windows DPAPI; public key saved.");return;
}
if(args.Length==5 && args[0]=="sign")
{
    using var rsa=RSA.Create();var secret=ProtectedData.Unprotect(File.ReadAllBytes(args[1]),null,DataProtectionScope.CurrentUser);
    try{rsa.ImportPkcs8PrivateKey(secret,out _);}finally{CryptographicOperations.ZeroMemory(secret);}
    if(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())!=UpdateTrust.PublicKey)throw new InvalidOperationException("Signing key does not match trusted public key.");
    if(!Version.TryParse(args[3].TrimStart('v'),out var version))throw new ArgumentException("Invalid version.");
    var asset=Path.GetFileName(args[2]);if(!UpdateTrust.AllowedAsset(asset))throw new ArgumentException("Invalid release asset.");
    using var exe=File.OpenRead(args[2]);var hash=Convert.ToHexString(SHA256.HashData(exe));var manifest=new UpdateManifest(1,Product.Repository,version.ToString(),asset,exe.Length,hash,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddYears(1));
    var data=JsonSerializer.SerializeToUtf8Bytes(manifest);var signature=Convert.ToBase64String(rsa.SignData(data,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
    UpdateTrust.Verify(data,signature,version.ToString(),hash,exe.Length,asset:asset);Directory.CreateDirectory(args[4]);File.WriteAllBytes(Path.Combine(args[4],asset+".manifest.json"),data);File.WriteAllText(Path.Combine(args[4],asset+".manifest.sig"),signature);File.WriteAllText(Path.Combine(args[4],asset+".sha256"),hash+"  "+asset+"\n");Console.WriteLine("Release manifest signed and verified.");return;
}
Console.WriteLine("init <protected-key> <public-key-file> | sign <protected-key> <exe> <version> <output-directory>");Environment.ExitCode=1;
