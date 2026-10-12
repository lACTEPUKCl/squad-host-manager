using SquadHostManager.Core;
using Xunit;

namespace SquadHostManager.Tests;
public class ReleaseNotesTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void EveryLanguageGetsOnlyItsSection(string newline)
    {
        string[] languages=["ru","en","de","es","fr","zh-Hans","tr"];
        var body=string.Join(newline+newline,languages.Select(l=>"## "+l+newline+"Changes: "+l));
        foreach(var language in languages)Assert.Equal("Changes: "+language,ReleaseNotes.ForLanguage(body,language));
    }
    [Fact] public void MissingLanguageFallsBackToEnglish()=>Assert.Equal("English",ReleaseNotes.ForLanguage("## ru\nRussian\n## en\nEnglish","de"));
    [Fact] public void NoFallbackDoesNotExposeOtherLanguages()=>Assert.Equal("",ReleaseNotes.ForLanguage("## ru\nRussian","de"));
    [Fact] public void PlainDescriptionIsPreserved()=>Assert.Equal("Fixed startup.",ReleaseNotes.ForLanguage(" Fixed startup. ","en"));
    [Fact] public void WhitespaceAndHeadingCaseAreAccepted()=>Assert.Equal("Fixed.",ReleaseNotes.ForLanguage("## ZH-HANS  \r\nFixed.\r\n## en\r\nOther.","zh-Hans"));
}
