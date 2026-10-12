using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SquadHostManager.Core;
public static class HostingTransport
{
    public static bool AcceptCertificate(X509Certificate2? certificate,SslPolicyErrors errors,string? fingerprint)
    {
        if(fingerprint==null)return errors==SslPolicyErrors.None;
        if(certificate==null || fingerprint.Length!=64 || fingerprint.Any(c=>!Uri.IsHexDigit(c)) || DateTime.UtcNow<certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow>certificate.NotAfter.ToUniversalTime())return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData),Convert.FromHexString(fingerprint));
    }
    public static HttpClientHandler Handler(Uri endpoint,string? fingerprint)
    {
        var handler=new HttpClientHandler{AllowAutoRedirect=false};
        if(fingerprint!=null)handler.ServerCertificateCustomValidationCallback=(request,certificate,_,errors)=>request.RequestUri?.Host==endpoint.Host && request.RequestUri.Port==endpoint.Port && AcceptCertificate(certificate,errors,fingerprint);
        return handler;
    }
    public static HttpClient Client(Uri endpoint,string? fingerprint,TimeSpan? timeout=null)=>new(Handler(endpoint,fingerprint)){BaseAddress=endpoint,Timeout=timeout??TimeSpan.FromSeconds(45)};
    public static byte[] CreateCertificate(string address,string password)
    {
        using var key=RSA.Create(3072);
        var request=new CertificateRequest("CN=Squad Host Manager",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var names=new SubjectAlternativeNameBuilder();if(IPAddress.TryParse(address,out var ip))names.AddIpAddress(ip);else names.AddDnsName(address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new System.Security.Cryptography.OidCollection{new("1.3.6.1.5.5.7.3.1")},false));
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10),DateTimeOffset.UtcNow.AddYears(5));
        return certificate.Export(X509ContentType.Pfx,password);
    }
}
