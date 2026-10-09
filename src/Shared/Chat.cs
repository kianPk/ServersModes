using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace ServersModes.Shared;

public static class Chat
{
    public static string Tag { get; set; } = "Servers";

    private static string Prefix => $" {ChatColors.Green}[{Tag}]{ChatColors.Default} ";

    public static void All(string message) => Server.PrintToChatAll(Prefix + message);

    public static void To(CCSPlayerController player, string message)
    {
        if (player.IsValid)
        {
            player.PrintToChat(Prefix + message);
        }
    }
}

public static class Players
{
    public static IEnumerable<CCSPlayerController> Humans() =>
        Utilities
            .GetPlayers()
            .Where(player =>
                player is { IsValid: true, IsBot: false, IsHLTV: false }
                && player.Connected == PlayerConnectedState.Connected
            );

    public static bool IsHuman(CCSPlayerController? player) =>
        player is { IsValid: true, IsBot: false, IsHLTV: false };

    public static CCSGameRules? Rules() =>
        Utilities
            .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()
            ?.GameRules;

    public static bool IsWarmup() => Rules()?.WarmupPeriod ?? false;

    public static int Score(CsTeam team) =>
        Utilities
            .FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager")
            .FirstOrDefault(entry => entry.TeamNum == (int)team)
            ?.Score ?? 0;
}
