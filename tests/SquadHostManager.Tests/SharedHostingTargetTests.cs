using SquadHostManager.Core;
namespace SquadHostManager.Tests;
public class SharedHostingTargetTests
{
    [Fact] public void InstallationUsesAnIsolatedPortAndDirectory()
    {
        var target=new SharedHostingTarget("192.0.2.1",22,"root",18443);target.Validate();
        Assert.Equal("/opt/squad-host-manager-app-18443",target.Root);Assert.Equal("https://192.0.2.1:18443/",target.Endpoint.ToString());
        var ipv6=target with{Address="2001:db8::1"};ipv6.Validate();Assert.Equal("https://[2001:db8::1]:18443/",ipv6.Endpoint.ToString());
    }
    [Theory]
    [InlineData("https://example.com/","root",22,18443)]
    [InlineData("127.0.0.1","root",22,18443)]
    [InlineData("localhost","root",22,18443)]
    [InlineData("0.0.0.0","root",22,18443)]
    [InlineData("192.0.2.1; rm -rf /","root",22,18443)]
    [InlineData("192.0.2.1","root;whoami",22,18443)]
    [InlineData("192.0.2.1","root",0,18443)]
    [InlineData("192.0.2.1","root",22,443)]
    [InlineData("192.0.2.1","root",18443,18443)]
    [InlineData("192.0.2.1","root",22,65536)]
    public void InvalidAddressesPortsAndShellInputAreRejected(string address,string user,int ssh,int port)=>Assert.Throws<ArgumentException>(()=>new SharedHostingTarget(address,ssh,user,port).Validate());
}
