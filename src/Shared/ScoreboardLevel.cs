using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.UserMessages;

namespace ServersModes.Shared;

// Faceit-style levels 1–10 on the TAB scoreboard. Rank[5] is the pin slot;
// with the cs2-FaceitLevels workshop addon (3724637448) those pins render as
// the Faceit level badges. Without the addon they still show as stock medals.
public static class ScoreboardLevel
{
    // Pin IDs the FaceitLevels workshop remaps to level images (Staaar0 mapping).
    private static readonly MedalRank_t[] Pins =
    [
        default,
        (MedalRank_t)1017,
        (MedalRank_t)1032,
        (MedalRank_t)1019,
        (MedalRank_t)1005,
        (MedalRank_t)1051,
        (MedalRank_t)1007,
        (MedalRank_t)1020,
        (MedalRank_t)1082,
        (MedalRank_t)1035,
        (MedalRank_t)1060,
    ];

    private const int RankIndex = 5;

    // Faceit's own Elo bands, so a fresh player at 1000 sits on level 4.
    public static int FromElo(double elo) =>
        elo switch
        {
            < 501 => 1,
            < 751 => 2,
            < 901 => 3,
            < 1051 => 4,
            < 1201 => 5,
            < 1351 => 6,
            < 1531 => 7,
            < 1751 => 8,
            < 2001 => 9,
            _ => 10,
        };

    public static void Apply(CCSPlayerController player, int level)
    {
        if (!player.IsValid || player.InventoryServices is not { } inventory)
        {
            return;
        }

        var ranks = inventory.Rank;
        if (ranks.Length <= RankIndex)
        {
            return;
        }

        level = Math.Clamp(level, 1, 10);
        var pin = Pins[level];
        if (ranks[RankIndex] == pin)
        {
            return;
        }

        ranks[RankIndex] = pin;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
    }

    public static void RevealAll()
    {
        try
        {
            using var message = UserMessage.FromPartialName("CCSUsrMsg_ServerRankRevealAll");
            message.Recipients.AddAllPlayers();
            message.Send();
        }
        catch
        {
            // Older CSS builds have no partial name; the pins still show for
            // the player's own client without it.
        }
    }
}
