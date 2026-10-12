using System.Security.Cryptography;
using System.Text.Json;
using SquadHostManager.Core;
using Xunit;

namespace SquadHostManager.Tests;

public class ComponentTrustTests
{
    [Theory]
    [InlineData("SquadHostManager-Full.exe")]
    [InlineData("SquadHostManager.Service-win-x64.exe")]
    [InlineData("SquadHostManager.Service-linux-x64")]
    [InlineData("SquadHostManager.Service-linux-arm64")]
    public void SignedComponentCannotBeSubstitutedForAnotherAsset(string asset)
    {
        using var key=RSA.Create(2048);
        var manifest=new UpdateManifest(1,Product.Repository,"0.1.1",asset,128,new string('A',64),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddDays(1));
        var bytes=JsonSerializer.SerializeToUtf8Bytes(manifest);
        var signature=Convert.ToBase64String(key.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
        var publicKey=Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        Assert.Equal(manifest,UpdateTrust.Verify(bytes,signature,"0.1.1",manifest.Sha256,128,publicKey,asset));
        Assert.Throws<InvalidOperationException>(()=>UpdateTrust.Verify(bytes,signature,"0.1.1",manifest.Sha256,128,publicKey));
        Assert.Throws<InvalidOperationException>(()=>UpdateTrust.Verify(bytes,signature,"0.1.1",new string('B',64),128,publicKey,asset));
    }
    [Theory]
    [InlineData("../SquadHostManager.exe")]
    [InlineData("other.exe")]
    public void ArbitraryComponentNamesAreRejected(string asset)=>Assert.False(UpdateTrust.AllowedAsset(asset));
}
