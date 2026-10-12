using System.Text.RegularExpressions;

namespace SquadHostManager.Core;
public static class ReleaseNotes
{
    public static string ForLanguage(string body,string language)
    {
        body=body.Replace("\r\n","\n").Replace('\r','\n');
        var sections=Regex.Matches(body,@"^##[ \t]+(?<language>ru|en|de|es|fr|zh-Hans|tr)[ \t]*$",RegexOptions.Multiline|RegexOptions.IgnoreCase);
        if(sections.Count==0)return body.Trim();
        var selected=sections.Cast<Match>().FirstOrDefault(m=>string.Equals(m.Groups["language"].Value,language,StringComparison.OrdinalIgnoreCase))
            ??sections.Cast<Match>().FirstOrDefault(m=>string.Equals(m.Groups["language"].Value,"en",StringComparison.OrdinalIgnoreCase));
        if(selected==null)return "";
        var next=sections.Cast<Match>().FirstOrDefault(m=>m.Index>selected.Index);
        var start=selected.Index+selected.Length;
        return body[start..(next?.Index??body.Length)].Trim();
    }
}
