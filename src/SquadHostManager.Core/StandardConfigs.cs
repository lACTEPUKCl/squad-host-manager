namespace SquadHostManager.Core;

public static class StandardConfigs
{
    private static readonly IReadOnlyDictionary<string,string> Files=Load();
    private static IReadOnlyDictionary<string,string> Load()
    {
        var assembly=typeof(StandardConfigs).Assembly;
        const string prefix="SquadHostManager.Core.StandardConfigs.";
        return assembly.GetManifestResourceNames().Where(n=>n.StartsWith(prefix,StringComparison.Ordinal)).ToDictionary(n=>n[prefix.Length..],n=>
        {
            using var reader=new StreamReader(assembly.GetManifestResourceStream(n)!);
            return reader.ReadToEnd();
        },StringComparer.Ordinal);
    }
    public static IEnumerable<string> Names=>Files.Keys;
    public static string Get(string file)=>file=="Server.cfg"?SharedServer(Files[file]):Files.GetValueOrDefault(file,"");
    public static string SharedServer(string text)=>SquadConfig.Remove(SquadConfig.Remove(text,"ServerName"),"ServerPassword");
    public static Dictionary<string,string> Copy()=>Files.Keys.ToDictionary(n=>n,Get,StringComparer.Ordinal);
}
