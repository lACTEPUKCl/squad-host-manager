using System.Text.Json;
using SquadHostManager.Core;
using Xunit;

namespace SquadHostManager.Tests;
public class ConnectionBackupTests
{
    private static SharedHostingProfile Profile=>new("https://example.org:18443/",new string('A',64),"fixture-owner-password","SHA256:fixture","Example");
    [Fact] public void BackupPreservesCredentialsAndCertificatePin()=>Assert.Equal(Profile,HostingConnectionBackup.Parse(JsonSerializer.Serialize(Profile)));
    [Theory]
    [InlineData("http://example.org/")]
    [InlineData("https://127.0.0.1:18443/")]
    [InlineData("https://user:password@example.org/")]
    [InlineData("https://example.org/?query=1")]
    public void InvalidEndpointsAreRejected(string endpoint)=>Assert.Throws<ArgumentException>(()=>HostingConnectionBackup.Parse(JsonSerializer.Serialize(Profile with{Endpoint=endpoint})));
    [Fact] public void PublicCertificateConnectionKeepsNormalTlsValidation()=>Assert.Null(HostingConnectionBackup.Parse(JsonSerializer.Serialize(Profile with{CertificateSha256=null})).CertificateSha256);
    [Fact] public void MissingPinIsRejected()=>Assert.Throws<ArgumentException>(()=>HostingConnectionBackup.Parse(JsonSerializer.Serialize(Profile with{CertificateSha256=""})));
    [Fact] public void MissingPasswordIsRejected()=>Assert.Throws<ArgumentException>(()=>HostingConnectionBackup.Parse(JsonSerializer.Serialize(Profile with{AdminPassword=""})));
}
