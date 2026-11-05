using System.Text.Json;
using System.Text.RegularExpressions;
namespace SquadHostManager.Service;
public sealed partial class RespawnHost
{
    private async Task<string?> ErrorDetail(HttpResponseMessage response,CancellationToken ct)
    {
        try
        {
            await using var input=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();var buffer=new byte[4096];
            while(output.Length<16384){var read=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,16384-output.Length)),ct);if(read==0)break;output.Write(buffer,0,read);}
            using var json=JsonDocument.Parse(output.ToArray());var root=json.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)return null;
            var message=Text(root,"message")??Text(root,"error")??Text(root,"title");
            if(string.IsNullOrWhiteSpace(message))return null;
            if(!string.IsNullOrEmpty(Token))message=message.Replace(Token,"[redacted]",StringComparison.Ordinal);
            message=Regex.Replace(message,@"rsp_[a-fA-F0-9]{64}|https?://\S+|(?i:Bearer)\s+\S+","[redacted]");
            message=Regex.Replace(message,@"(?i)(password|token|api[_-]?key|secret)\s*[:=]\s*\S+","$1=[redacted]");
            message=Regex.Replace(message,@"\s+"," ").Trim();message=new string(message.Where(c=>!char.IsControl(c)).ToArray());
            return message.Length>500?message[..500]+"…":message;
        }
        catch(OperationCanceledException){throw;}
        catch(Exception){return null;}
    }
}
