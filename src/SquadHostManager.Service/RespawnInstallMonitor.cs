using System.Net.WebSockets;
using System.Text.Json;
namespace SquadHostManager.Service;

public interface IRespawnInstallMonitor:IAsyncDisposable
{
    Task Wait(CancellationToken ct);
}

public sealed class RespawnInstallMonitor:IRespawnInstallMonitor
{
    private readonly ClientWebSocket socket=new();
    private readonly CancellationTokenSource lifetime=new();
    private readonly TaskCompletionSource authenticated=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? receiver;
    public static async Task<RespawnInstallMonitor> Open(string address,string token,CancellationToken ct)
    {
        var uri=new Uri(address);
        if(uri.Scheme!="wss" || !uri.Host.EndsWith(".respawnhost.com",StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length!=0 || uri.Query.Length!=0)
            throw new IOException("RespawnHost вернул недопустимый адрес журнала установки.");
        var result=new RespawnInstallMonitor();
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await result.socket.ConnectAsync(uri,timeout.Token);
            await result.socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new{ @event="auth",args=new[]{token}}),WebSocketMessageType.Text,true,timeout.Token);
            result.receiver=result.Receive();
            await result.authenticated.Task.WaitAsync(timeout.Token);return result;
        }
        catch{await result.DisposeAsync();throw;}
    }
    public Task Wait(CancellationToken ct)=>completed.Task.WaitAsync(ct);
    private async Task Receive()
    {
        try
        {
            var buffer=new byte[8192];
            while(!lifetime.IsCancellationRequested)
            {
                using var message=new MemoryStream();WebSocketReceiveResult part;
                do
                {
                    part=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),lifetime.Token);
                    if(part.MessageType==WebSocketMessageType.Close)throw new IOException("Соединение с журналом установки RespawnHost закрыто. Повторная установка автоматически не запускается.");
                    message.Write(buffer,0,part.Count);if(message.Length>1024*1024)throw new IOException("Слишком большое сообщение журнала установки.");
                }while(!part.EndOfMessage);
                using var doc=JsonDocument.Parse(message.ToArray());var root=doc.RootElement;
                var name=root.GetProperty("event").GetString();
                if(name=="auth success")authenticated.TrySetResult();
                if(name is "jwt error" or "token expired" or "auth error")throw new IOException("RespawnHost отклонил доступ к журналу установки.");
                if(name=="install completed")
                {
                    var args=root.GetProperty("args");
                    if(args.GetArrayLength()==0 || !string.Equals(args[0].ToString(),"true",StringComparison.OrdinalIgnoreCase))throw new IOException("RespawnHost сообщил об ошибке установки. Сервер не запущен.");
                    completed.TrySetResult();return;
                }
            }
        }
        catch(Exception e){authenticated.TrySetException(e);completed.TrySetException(e);}
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();socket.Abort();if(receiver!=null)await receiver;
        socket.Dispose();lifetime.Dispose();
    }
}
