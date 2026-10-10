using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace ServersModes.Shared;

// Nobody keeps a Medi-Shot, whoever hands it out: Deathmatch's kill rewards,
// a map's items or another plugin. A mode that hands out its own says which
// pickups are its own with keep.
public static class NoHealthshot
{
    public const string Healthshot = "weapon_healthshot";

    public static void Register(BasePlugin plugin, Func<CCSPlayerController, bool>? keep = null) =>
        plugin.RegisterEventHandler<EventItemPickup>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } player && @event.Item.Contains("healthshot") && keep?.Invoke(player) != true)
            {
                Server.NextFrame(() =>
                {
                    if (player.IsValid)
                    {
                        player.RemoveItemByDesignerName(Healthshot);
                    }
                });
            }

            return HookResult.Continue;
        });
}
