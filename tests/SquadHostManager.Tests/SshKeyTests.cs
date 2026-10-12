using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public class SshKeyTests
{
    [Fact]
    public void PublicKeyMatchesPrivateKeyAndReloadsAfterReplacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "key");
            using var first = RSA.Create(2048);
            using var second = RSA.Create(2048);
            File.WriteAllText(path, first.ExportRSAPrivateKeyPem());
            File.WriteAllText(path + ".pub", "ssh-rsa stale-file");
            Assert.Equal(Expected(first), SshKeyMaterial.PublicKey(path));
            File.WriteAllText(path, second.ExportRSAPrivateKeyPem());
            Assert.Equal(Expected(second), SshKeyMaterial.PublicKey(path));
            Assert.NotEqual(Expected(first), SshKeyMaterial.PublicKey(path));
            Assert.Equal("ssh-rsa stale-file", File.ReadAllText(path + ".pub"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CommentsAndWhitespaceDoNotChangeKeyIdentity()
    {
        Assert.Equal("ssh-ed25519 AAAA", SshKeyMaterial.Identity("ssh-ed25519\tAAAA  old comment"));
    }

    private static string Expected(RSA rsa)
    {
        var p = rsa.ExportParameters(false);
        using var stream = new MemoryStream();
        void Field(byte[] data, bool number = false)
        {
            if (number && (data[0] & 0x80) != 0) data = [0, .. data];
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length); stream.Write(data);
        }
        Field(Encoding.ASCII.GetBytes("ssh-rsa")); Field(p.Exponent!, true); Field(p.Modulus!, true);
        return "ssh-rsa " + Convert.ToBase64String(stream.ToArray());
    }
}
