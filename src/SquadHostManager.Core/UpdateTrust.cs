using System.Security.Cryptography;
using System.Text.Json;

namespace SquadHostManager.Core;
public record UpdateManifest(int Format,string Product,string Version,string Asset,long Size,string Sha256,DateTimeOffset IssuedAt,DateTimeOffset ValidUntil);
public static class UpdateTrust
{
    public static bool AllowedAsset(string asset)=>asset is "SquadHostManager.exe" or "SquadHostManager-Full.exe" or "SquadHostManager.Service-win-x64.exe" or "SquadHostManager.Service-linux-x64" or "SquadHostManager.Service-linux-arm64";
    public const string PublicKey="MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAyTtk4hZINUU8vSYfBDj/vXPuQwV4TNLj+GVP7+IlTURI1Y71740Vj+o7LLMTu3OwlLGzjr7wXe0Hc2npaNSe5+cAHE26PZwhXPCrcw2a8Rn8EshwBWvSzV7u6iFACmVJRKw6pojBGgOozEV+DiFIMiWoP2bQQHX4/y3QDeMZofWVWtQ019U7tdqGgV0JVQ1OtTa7NpsHSY27ibx7JWuXELoPnm4HEkP8XjNLWIJZkMUPL+1I1+zRe1gunvFXdl3PyRD1+VyzFkaMgijcaFf7AH4M8+ac/omSVYjlVkOPqhIMsJmfPIhgw8RbwWlrS6gIT6hKfi+DbbNoisYX7nTIYjPW85mbN6fAxZfduKx1Cd34yRu84JS3NtMzXA7d9STfst0wAC7ZXskbUF0PtRC1keL4qEs0EEL7sunUitIixmsSNPtDGqafaKPzjgPtCfH7I4ZXkngFne9Ve/XyYdTym0T2Qgp+zkSAcuew91nMkLkecv+1KvrjriLr/XA2cn9pAgMBAAE=";
    public static UpdateManifest Verify(byte[] manifest,string signature,string version,string sha256,long size,string publicKey=PublicKey,string asset="SquadHostManager.exe")
    {
        if(manifest.Length>8192 || signature.Length>2048)throw new InvalidOperationException("Недопустимая подпись обновления.");
        try
        {
            using var rsa=RSA.Create();rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey),out _);
            if(!rsa.VerifyData(manifest,Convert.FromBase64String(signature.Trim()),HashAlgorithmName.SHA256,RSASignaturePadding.Pss))throw new InvalidOperationException("Подпись обновления не подтверждена.");
            var data=JsonSerializer.Deserialize<UpdateManifest>(manifest)??throw new InvalidOperationException("Нет данных обновления.");
            if(data.Format!=1 || data.Product!=Product.Repository || data.Asset!=asset || !AllowedAsset(asset) || data.Version!=version || data.Size!=size || !string.Equals(data.Sha256,sha256,StringComparison.OrdinalIgnoreCase) || data.IssuedAt>DateTimeOffset.UtcNow.AddMinutes(5) || data.ValidUntil<=DateTimeOffset.UtcNow || data.ValidUntil<=data.IssuedAt)throw new InvalidOperationException("Подписанное обновление не соответствует файлу, версии или сроку действия.");
            return data;
        }
        catch(Exception error) when(error is CryptographicException or FormatException or JsonException){throw new InvalidOperationException("Не удалось проверить подпись обновления.",error);}
    }
}
