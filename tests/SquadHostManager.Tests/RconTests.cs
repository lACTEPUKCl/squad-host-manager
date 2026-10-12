using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public class RconTests
{
    private static byte[] Packet(int id, int type, string body)
    {
        var text = Encoding.UTF8.GetBytes(body); var packet = new byte[text.Length + 14];
        BinaryPrimitives.WriteInt32LittleEndian(packet, text.Length + 10); BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type); text.CopyTo(packet.AsSpan(12)); return packet;
    }
    private static async Task<(int Id, int Type, string Body)> Read(NetworkStream stream, CancellationToken ct)
    {
        var head = new byte[4]; await stream.ReadExactlyAsync(head, ct); var data = new byte[BinaryPrimitives.ReadInt32LittleEndian(head)]; await stream.ReadExactlyAsync(data, ct);
        return (BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)), Encoding.UTF8.GetString(data, 8, data.Length - 10));
    }
    [Fact]
    public async Task FragmentedPacketsAndMultipartResponseAreAssembled()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct); using var stream = client.GetStream();
            Assert.Equal((1, 3, "password"), await Read(stream, ct));
            async Task Send(byte[] bytes) { for (var i = 0; i < bytes.Length; i += 3) await stream.WriteAsync(bytes.AsMemory(i, Math.Min(3, bytes.Length - i)), ct); }
            await Send(Packet(1, 0, "")); await Send(Packet(1, 2, "")); Assert.Equal((2, 2, "ListPlayers"), await Read(stream, ct)); Assert.Equal(3, (await Read(stream, ct)).Id);
            await Send(Packet(2, 0, "Игрок 1\n")); await Send(Packet(2, 0, "Игрок 2")); await Send(Packet(3, 0, ""));
        }, ct);
        Assert.Equal("Игрок 1\nИгрок 2", await Rcon.Execute("127.0.0.1", port, "password", "ListPlayers", ct)); await server;
    }
    [Fact]
    public async Task AuthenticationFailureDoesNotSendAdminCommand()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () => { using var client = await listener.AcceptTcpClientAsync(ct); using var stream = client.GetStream(); await Read(stream, ct); await stream.WriteAsync(Packet(-1, 2, ""), ct); }, ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Rcon.Execute("127.0.0.1", port, "bad", "AdminEndMatch", ct)); await server;
    }
    [Fact]
    public async Task PersistentConnectionAuthenticatesOnceAndIgnoresLateMarkerPackets()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));var ct=timeout.Token;
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
        var server=Task.Run(async()=>
        {
            using var client=await listener.AcceptTcpClientAsync(ct);using var stream=client.GetStream();
            Assert.Equal((1,3,"password"),await Read(stream,ct));await stream.WriteAsync(Packet(1,2,""),ct);
            for(var i=0;i<2;i++)
            {
                var cmd=await Read(stream,ct);Assert.Equal((2+i*2,2,"ListPlayers"),cmd);var marker=await Read(stream,ct);
                await stream.WriteAsync(Packet(cmd.Id,0,"Players "+i),ct);await stream.WriteAsync(Packet(marker.Id,0,""),ct);
                await stream.WriteAsync(Packet(marker.Id,0,"late"),ct);
                await stream.WriteAsync(Packet(marker.Id,0,""),ct);
                foreach(var b in new byte[]{0,1,0,0,0,0,0})await stream.WriteAsync(new[]{b},ct);
            }
        },ct);
        using var connection=new RconConnection();Assert.Equal("Players 0",await connection.Execute("127.0.0.1",port,"password","ListPlayers",ct));
        Assert.Equal("Players 1",await connection.Execute("127.0.0.1",port,"password","ListPlayers",ct));await server;
    }
    [Fact]
    public async Task LostResponseDoesNotReplayAdminCommand()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));var ct=timeout.Token;
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;var received=0;
        var server=Task.Run(async()=>
        {
            using var client=await listener.AcceptTcpClientAsync(ct);using var stream=client.GetStream();await Read(stream,ct);await stream.WriteAsync(Packet(1,2,""),ct);
            Assert.Equal("AdminEndMatch",(await Read(stream,ct)).Body);received++;await Read(stream,ct);
        },ct);
        using var connection=new RconConnection();await Assert.ThrowsAnyAsync<IOException>(()=>connection.Execute("127.0.0.1",port,"password","AdminEndMatch",ct));await server;
        Assert.Equal(1,received);Assert.False(connection.Connected);Assert.False(listener.Pending());
    }

    [Fact]
    public async Task ReadOnlyQueryReconnectsAfterServerClosesConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct=timeout.Token;
        using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        var server=Task.Run(async()=>
        {
            for(var attempt=0;attempt<2;attempt++)
            {
                using var client=await listener.AcceptTcpClientAsync(ct); using var stream=client.GetStream();
                Assert.Equal((1,3,"password"),await Read(stream,ct)); await stream.WriteAsync(Packet(1,2,""),ct);
                var cmd=await Read(stream,ct); Assert.Equal("ListPlayers",cmd.Body); var marker=await Read(stream,ct);
                if(attempt==1) { await stream.WriteAsync(Packet(cmd.Id,0,"Active Players"),ct); await stream.WriteAsync(Packet(marker.Id,0,""),ct); }
            }
        },ct);
        using var connection=new RconConnection();
        Assert.Equal("Active Players",await connection.Execute("127.0.0.1",port,"password","ListPlayers",ct));
        await server;
    }

    [Fact]
    public async Task Ordinary256BytePacketAfterSingleMarkerIsNotASevenByteTrailer()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));var ct=timeout.Token;
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
        var server=Task.Run(async()=>{
            using var client=await listener.AcceptTcpClientAsync(ct);using var stream=client.GetStream();
            await Read(stream,ct);await stream.WriteAsync(Packet(1,2,""),ct);
            for(var i=0;i<2;i++){
                var cmd=await Read(stream,ct);var marker=await Read(stream,ct);
                await stream.WriteAsync(Packet(cmd.Id,0,"OK"),ct);await stream.WriteAsync(Packet(marker.Id,0,""),ct);
                if(i==0)await stream.WriteAsync(Packet(0,1,new string('x',246)),ct);
            }
        },ct);
        using var connection=new RconConnection();
        Assert.Equal("OK",await connection.Execute("127.0.0.1",port,"password","ListPlayers",ct));
        Assert.Equal("OK",await connection.Execute("127.0.0.1",port,"password","ListPlayers",ct));await server;
    }

}
