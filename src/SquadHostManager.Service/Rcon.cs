using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace SquadHostManager.Service;

public static class Rcon
{
    public static async Task<string> Execute(string host, int port, string password, string command, CancellationToken cancellation)
    {
        using var connection = new RconConnection();
        return await connection.Execute(host, port, password, command, cancellation);
    }
}

public sealed class RconConnection : IDisposable
{
    private TcpClient? tcp;
    private int nextId = 2;
    private bool afterEmptyMarker;
    private readonly HashSet<int> markerIds = new();
    private readonly HashSet<int> emptyMarkers = new();
    private readonly SemaphoreSlim gate = new(1);
    public bool Connected => tcp?.Connected == true;
    public void Dispose() { tcp?.Dispose(); tcp = null; }
    public async Task<string> Execute(string host, int port, string password, string command, CancellationToken cancellation)
    {
        try { return await ExecuteOnce(host, port, password, command, cancellation); }
        catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or SocketException or TimeoutException && command is "ListPlayers" or "ShowCurrentMap" or "ShowNextMap" or "ListLayers" or "ListCommands 1")
        { return await ExecuteOnce(host, port, password, command, cancellation); }
    }
    private async Task<string> ExecuteOnce(string host, int port, string password, string command, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(12)); var ct = timeout.Token;
        if (tcp != null && (!tcp.Connected || tcp.Client.Poll(0, SelectMode.SelectRead) && tcp.Client.Available == 0)) Dispose();
        var authenticate = tcp == null;
        if (authenticate) { tcp = new TcpClient { NoDelay=true }; afterEmptyMarker=false; emptyMarkers.Clear(); await tcp.ConnectAsync(host, port, ct); }
        var stream = tcp!.GetStream();
        async Task Send(int id, int type, string body)
        {
            var text = Encoding.UTF8.GetBytes(body); var bytes = new byte[text.Length + 14];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, text.Length + 10); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), id);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), type); text.CopyTo(bytes.AsSpan(12)); await stream.WriteAsync(bytes, ct);
        }
        async Task<(int Id, int Type, string Body)> Read()
        {
            var length = new byte[4]; await stream.ReadExactlyAsync(length, ct); var size = BinaryPrimitives.ReadInt32LittleEndian(length);
            byte[] prefix=[];
            // Squad appends 00 01 00 00 00 00 00 after an empty marker packet.
            // It is outside the advertised packet length and may arrive fragmented.
            if(afterEmptyMarker && size==256)
            {
                prefix=new byte[3];await stream.ReadExactlyAsync(prefix,ct);afterEmptyMarker=false;
                if(prefix.All(b=>b==0))return await Read();
            }
            if (size is < 10 or > 1024 * 1024) throw new IOException("Некорректный RCON-пакет.");
            var bytes = new byte[size];prefix.CopyTo(bytes,0);await stream.ReadExactlyAsync(bytes.AsMemory(prefix.Length), ct);
            var packet=(Id:BinaryPrimitives.ReadInt32LittleEndian(bytes),Type:BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)),Body:Encoding.UTF8.GetString(bytes, 8, size - 10));
            var marker=packet.Type==0 && packet.Body.Length==0 && markerIds.Contains(packet.Id);
            afterEmptyMarker=marker && !emptyMarkers.Add(packet.Id);
            return packet;
        }
        if (authenticate)
        {
            await Send(1, 3, password);
            while (true) { var p = await Read(); if (p.Id == -1) throw new UnauthorizedAccessException("RCON отклонил пароль."); if (p.Type == 2 && p.Id == 1) break; }
        }
        var commandId = nextId; var markerId = nextId + 1; nextId += 2;
        markerIds.Add(markerId); markerIds.RemoveWhere(id=>id<markerId-128);emptyMarkers.RemoveWhere(id=>id<markerId-128);
        await Send(commandId, 2, command); await Send(markerId, 0, "");
        var output = new StringBuilder();
        while (true) { var p = await Read(); if (p.Id == markerId) break; if (p.Id == commandId) output.Append(p.Body); if (output.Length > 1024 * 1024) throw new IOException("RCON-ответ слишком большой."); }
        return output.ToString();
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { Dispose(); throw new TimeoutException("Игровой сервер не ответил на RCON вовремя. Проверьте состояние перед повтором команды."); }
        catch { Dispose(); throw; }
        finally { gate.Release(); }
    }
}
