using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace ServersModes.Shared;

// Nobody keeps a Medi-Shot, whoever hands it out: Deathmatch's kill rewards,
// a map's items or another plugin.
public static class NoHealthshot
{
    private const string Healthshot = "weapon_healthshot";

    public static void Register(BasePlugin plugin) =>
        plugin.RegisterEventHandler<EventItemPickup>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } player && @event.Item.Contains("healthshot"))
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
