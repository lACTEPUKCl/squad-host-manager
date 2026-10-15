using SquadHostManager.Core;

namespace SquadHostManager.Tests;

public class RconCommandTests
{
    private const string SteamId="76561198000000001";
    [Theory]
    [InlineData("AdminKick","AdminKick \"76561198000000001\" Team killing")]
    [InlineData("AdminWarn","AdminWarn \"76561198000000001\" Team killing")]
    [InlineData("AdminBan","AdminBan \"76561198000000001\" 1m Team killing")]
    [InlineData("AdminForceTeamChange","AdminForceTeamChange \"76561198000000001\"")]
    public void PlayerActionsUseSteamIdentity(string action,string expected)=>Assert.Equal(expected,RconCommands.PlayerAction(action,SteamId,"Team killing","1m"));
    [Theory]
    [InlineData("0")][InlineData("1m")][InlineData("2h")][InlineData("1d")][InlineData("1w")][InlineData("1M")][InlineData("1y")]
    public void BanDurationSupportsSquadUnits(string duration)=>Assert.True(RconCommands.ValidBanDuration(duration));
    [Theory]
    [InlineData("-1")][InlineData("1D")][InlineData("1d\nAdminEndMatch")][InlineData("99999999999")]
    public void RejectsInvalidBanDuration(string duration)=>Assert.False(RconCommands.ValidBanDuration(duration));
    [Fact] public void MissingSteamIdentityNeverFallsBackToReusedPlayerNumber()=>Assert.Throws<ArgumentException>(()=>RconCommands.PlayerAction("AdminKick","3","Reason"));
    [Fact] public void ControlCharactersCannotCreateAnotherCommand()=>Assert.Throws<ArgumentException>(()=>RconCommands.PlayerAction("AdminKick",SteamId,"Reason\nAdminEndMatch"));
    [Fact] public void CatalogPreservesQuotedPlayerNamesAndReasons()=>Assert.Equal("AdminBan \"Player With Spaces\" \"1m\" Reason with spaces",RconCommands.FromUsage("AdminBan","AdminBan \"<NameOrEOSId>\" \"<BanLength>\" <BanReason>",["Player With Spaces","1m","Reason with spaces"]));
    [Fact] public void OptionalFactionParametersAreOmittedWithoutLiteralBrackets()=>Assert.Equal("AdminChangeLayer AlBasrah_AAS_v1",RconCommands.FromUsage("AdminChangeLayer","AdminChangeLayer <LayerName> [<FactionSetup1>] [<FactionSetup2>]",["AlBasrah_AAS_v1","",""]));
    [Fact] public void OptionalFactionCannotShiftToTheWrongTeam()=>Assert.Throws<ArgumentException>(()=>RconCommands.FromUsage("AdminChangeLayer","AdminChangeLayer <LayerName> [<FactionSetup1>] [<FactionSetup2>]",["AlBasrah_AAS_v1","","USA"]));
    [Fact] public void CatalogDistinguishesSteamIdentityFromNumericPlayerId()=>Assert.Throws<ArgumentException>(()=>RconCommands.FromUsage("AdminKickById","AdminKickById <PlayerId> <KickReason>",[SteamId,"Reason"]));
    [Fact] public void CatalogRejectsMissingRequiredParameter()=>Assert.Throws<ArgumentException>(()=>RconCommands.FromUsage("AdminKick","AdminKick <NameOrSteamId> <KickReason>",[SteamId,""]));
    [Fact] public void CommandsWithoutParametersKeepTheirName()=>Assert.Equal("AdminEndMatch",RconCommands.FromUsage("AdminEndMatch","AdminEndMatch",[]));
    [Fact] public void OptionalParametersAreMarkedInTheEditor(){var parameters=RconCommands.Parameters("AdminChangeLayer <LayerName> [<FactionSetup1>]");Assert.False(parameters[0].Optional);Assert.True(parameters[1].Optional);Assert.Equal("FactionSetup1",parameters[1].Name);}
    [Theory]
    [InlineData("Unable to find player")][InlineData("AdminKick: Failed to find player")][InlineData("Unknown command: AdminKick")][InlineData("Permission denied")][InlineData("ERROR: invalid command")]
    public void DetectsExplicitServerRejections(string reply)=>Assert.True(RconCommands.Rejected(reply));
    [Theory]
    [InlineData("")][InlineData("Kicked player 3 for reason: Permission denied")][InlineData("----- Active Players -----")][InlineData("Remote admin has warned player Test. Message was \"Error\"")]
    public void DoesNotMisreadSuccessOrMissingConfirmation(string reply)=>Assert.False(RconCommands.Rejected(reply));
}
