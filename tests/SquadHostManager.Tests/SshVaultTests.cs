using System.Security.Cryptography;
using System.Text.Json;
using System.Net;
using Microsoft.Extensions.Configuration;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public class SshVaultTests
{
    [Fact]
    public void MissingOriginalUsesDurableCopyAfterRestart()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Directory, "primary");
        using var rsa = RSA.Create(2048); File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());
        var backup = new SshKeyVault(f.Store).Snapshot(path);
        var expected = SshKeyMaterial.PublicKey(path);
        File.Delete(path);
        Assert.Equal(expected, SshKeyMaterial.PublicKey(new SshKeyVault(f.Store).Snapshot(path)));
        Assert.NotEqual(path, backup);
    }

    [Fact]
    public void ChangingPrimaryRetainsOldPrivateKey()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Directory, "primary");
        using var first = RSA.Create(2048); using var second = RSA.Create(2048);
        File.WriteAllText(path, first.ExportRSAPrivateKeyPem());
        var vault = new SshKeyVault(f.Store); var old = vault.Snapshot(path);
        var oldPublic = SshKeyMaterial.PublicKey(old);
        File.WriteAllText(path, second.ExportRSAPrivateKeyPem()); var current = vault.Snapshot(path);
        Assert.NotEqual(old, current);
        Assert.Equal(oldPublic, SshKeyMaterial.PublicKey(old));
        Assert.Equal(2, vault.PreviousKeys().Count());
    }

    [Fact]
    public void MissingKeyWithoutBackupNeverGeneratesReplacement()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Directory, "missing");
        Assert.Throws<InvalidOperationException>(() => new SshKeyVault(f.Store).Snapshot(path));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderMismatchOrOmittedKeyBlocksPaidOrder(bool omit)
    {
        using var f = new Fixture(); var path = Path.Combine(f.Directory, "primary");
        using var rsa = RSA.Create(2048); File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());
        var primary = SshKeyMaterial.PublicKey(path);
        var handler = new KeyApi(primary, omit);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cherry:Token"] = "test",
            ["Ssh:KeyPath"] = path
        }).Build();
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.cherryservers.com/v1/") };
        var cherry = new CherryInfrastructure(cfg, f.Store, http);
        await Assert.ThrowsAsync<RentalRejectedException>(() => cherry.Rent("marker", default));
        Assert.Equal(0, handler.PaidOrders);
        Assert.Single(new SshKeyVault(f.Store).PreviousKeys());
    }

    private sealed class KeyApi(string primary, bool omit) : HttpMessageHandler
    {
        public int PaidOrders;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) PaidOrders++;
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/ssh-keys")
                ? JsonSerializer.Serialize(new[] { new { id = 42, key = primary } })
                : omit ? "{\"id\":42}" : "{\"id\":42,\"key\":\"ssh-rsa OTHER\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
