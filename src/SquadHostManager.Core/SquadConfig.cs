using System.Text.RegularExpressions;

namespace SquadHostManager.Core;

public static class SquadConfig
{
    public const string Initial = "ServerName=My Squad Server\nServerPassword=\nMaxPlayers=100\nShouldAdvertise=true\nIsLANMatch=false\nMapRotationMode=LayerList\nRecordDemos=false\n";
    public static string? Get(string text,string key)
    {
        var value=Regex.Matches(text,@"(?m)^[ \t]*"+Regex.Escape(key)+@"[ \t]*=([^\r\n]*)",RegexOptions.IgnoreCase).LastOrDefault()?.Groups[1].Value.Trim();
        if(value==null)return null;
        if(value.StartsWith('"') && value.LastIndexOf('"')>0)return value[1..value.LastIndexOf('"')];
        var comment=Regex.Match(value,@"[ \t]+//");
        return (comment.Success?value[..comment.Index]:value).Trim();
    }
    public static string Set(string text,string key,string value)
    {
        if(value.Any(c=>c is '\r' or '\n' or '\0'))throw new ArgumentException("Invalid value.");
        var pattern=@"(?im)^[ \t]*"+Regex.Escape(key)+@"[ \t]*=[^\r\n]*";
        var replaced=false;
        var result=Regex.Replace(text,pattern,_=>{if(replaced)return "";replaced=true;return key+"="+value;});
        return replaced?result:result.TrimEnd()+"\n"+key+"="+value+"\n";
    }
    public static string Identity(string text,string name,string password,int players) => Set(Set(Set(text,"ServerName",name),"ServerPassword",password),"MaxPlayers",players.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static string Remove(string text,string key)=>Regex.Replace(text,@"(?im)^[ \t]*"+Regex.Escape(key)+@"[ \t]*=[^\r\n]*\r?\n?","");
    public static void Validate(string text)
    {
        if(text.Length>20000 || text.Contains('\0'))throw new ArgumentException("Конфиг должен быть текстовым, не более 20000 символов.");
        foreach(var key in new[]{"Port","QueryPort","RconPort","RconPassword","BeaconPort","MultiHome"})
            if(Get(text,key)!=null)throw new ArgumentException("Порты и RCON настраиваются приложением: "+key);
        if(Get(text,"MaxPlayers") is {} players && (!int.TryParse(players,out var n) || n is <2 or >100))throw new ArgumentException("Лимит игроков: от 2 до 100.");
        if(Get(text,"ServerName") is {} name && (name.Length is <3 or >80 || name.Any(char.IsControl)))throw new ArgumentException("Название сервера: от 3 до 80 символов.");
        if(Get(text,"ServerPassword") is {} password && (password.Length>128 || password.Any(char.IsControl)))throw new ArgumentException("Пароль сервера: до 128 символов.");
    }
}
