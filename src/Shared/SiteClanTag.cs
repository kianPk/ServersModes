using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace ServersModes.Shared;

// Puts the site's address in every player's clan tag on TAB, so a Servers
// section server always advertises where to go.
public static class SiteClanTag
{
    public static void Register(BasePlugin plugin)
    {
        if (SiteHost.ClanTag() is null)
        {
            return;
        }

        plugin.RegisterEventHandler<EventPlayerConnectFull>((@event, _) =>
        {
            if (Players.IsHuman(@event.Userid))
            {
                Apply(@event.Userid!);
            }

            return HookResult.Continue;
        });

        plugin.RegisterEventHandler<EventPlayerSpawn>((@event, _) =>
        {
            if (Players.IsHuman(@event.Userid))
            {
                Apply(@event.Userid!);
            }

            return HookResult.Continue;
        });

        plugin.AddTimer(5f, Enforce, TimerFlags.REPEAT);
    }

    public static void Apply(CCSPlayerController player)
    {
        if (!Players.IsHuman(player) || SiteHost.ClanTag() is not { } tag)
        {
            return;
        }

        if (player.Clan == tag)
        {
            return;
        }

        player.Clan = tag;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
    }

    private static void Enforce()
    {
        foreach (var player in Players.Humans())
        {
            Apply(player);
        }
    }
}
