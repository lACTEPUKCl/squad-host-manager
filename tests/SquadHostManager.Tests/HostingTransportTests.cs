using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SquadHostManager.Core;
namespace SquadHostManager.Tests;
public class HostingTransportTests
{
    [Fact] public void SelfSignedCertificateIsAcceptedOnlyWithItsExactFingerprint()
    {
        using var cert=X509CertificateLoader.LoadPkcs12(HostingTransport.CreateCertificate("192.0.2.1","test-password"),"test-password",X509KeyStorageFlags.EphemeralKeySet);
        var fingerprint=Convert.ToHexString(SHA256.HashData(cert.RawData));
        Assert.False(HostingTransport.AcceptCertificate(cert,SslPolicyErrors.RemoteCertificateChainErrors,null));
        Assert.True(HostingTransport.AcceptCertificate(cert,SslPolicyErrors.RemoteCertificateChainErrors,fingerprint));
        Assert.True(HostingTransport.AcceptCertificate(cert,SslPolicyErrors.RemoteCertificateChainErrors,fingerprint.ToLowerInvariant()));
        Assert.False(HostingTransport.AcceptCertificate(cert,SslPolicyErrors.RemoteCertificateChainErrors,new string('0',64)));
        Assert.False(HostingTransport.AcceptCertificate(null,SslPolicyErrors.None,fingerprint));
        var invite=new ConnectionInvite("https://192.0.2.1:18443/","friend-key",fingerprint);
        Assert.Equal(invite,ConnectionInvite.Decode(invite.Encode()));
    }
    [Fact] public void ExpiredPinnedCertificateIsRejected()
    {
        using var key=RSA.Create(2048);var request=new CertificateRequest("CN=expired",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3),DateTimeOffset.UtcNow.AddDays(-1));
        Assert.False(HostingTransport.AcceptCertificate(cert,SslPolicyErrors.RemoteCertificateChainErrors,Convert.ToHexString(SHA256.HashData(cert.RawData))));
    }
    [Theory]
    [InlineData("https://192.0.2.1/","bad")]
    [InlineData("https://192.0.2.1/","zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("http://127.0.0.1/","aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void InvitationRejectsInvalidPin(string address,string pin)=>Assert.Throws<ArgumentException>(()=>ConnectionInvite.Decode(new ConnectionInvite(address,"key",pin).Encode()));
    [Fact] public void AuthenticatedRequestsNeverFollowRedirects()
    {using var handler=HostingTransport.Handler(new Uri("https://example.com/"),null);Assert.False(handler.AllowAutoRedirect);}
}
