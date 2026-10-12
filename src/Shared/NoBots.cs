using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace ServersModes.Shared;

// A mode server is for people only. The deathmatch game mode's cfg fills the
// server with bots on every map load, after any rules a plugin ran, so the
// quota is not enough on its own and any bot that still joins goes.
public static class NoBots
{
    public static void Register(BasePlugin plugin)
    {
        plugin.RegisterListener<Listeners.OnMapStart>(_ => Server.ExecuteCommand("bot_quota 0;bot_quota_mode normal"));
        plugin.AddTimer(1f, KickBots, TimerFlags.REPEAT);
    }

    private static void KickBots()
    {
        if (Utilities.GetPlayers().Any(player => player is { IsValid: true, IsBot: true, IsHLTV: false }))
        {
            Server.ExecuteCommand("bot_quota 0;bot_kick");
        }
    }
}
