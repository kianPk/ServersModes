using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace ServersModes.Shared;

// A mode server is for people only. The deathmatch game mode's cfg fills the
// server with bots on every map load, after any rules a plugin ran, so the
// quota has to be reasserted and any bot that still joins goes.
public static class NoBots
{
    public static void Register(BasePlugin plugin)
    {
        plugin.RegisterListener<Listeners.OnMapStart>(_ => KickBots());
        plugin.AddTimer(1f, KickBots, TimerFlags.REPEAT);
    }

    private static void KickBots()
    {
        // Always reassert the quota: Valve's deathmatch cfg can put it back
        // after the map starts, and waiting until a bot is seen lets one flash
        // on the scoreboard and jump around before the next tick.
        Server.ExecuteCommand("bot_quota 0;bot_quota_mode normal;bot_join_after_player 0;bot_auto_vacate 0;bot_kick");
    }
}
