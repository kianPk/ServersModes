using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace ServersModes.Shared;

// Killing a map's chicken has cut every sound for the rest of the map on a
// mode server, so no chicken is left standing.
public static class NoChickens
{
    private const string Chicken = "chicken";

    public static void Register(BasePlugin plugin)
    {
        plugin.RegisterListener<Listeners.OnEntitySpawned>(entity =>
        {
            if (entity.DesignerName == Chicken)
            {
                Server.NextFrame(() =>
                {
                    if (entity.IsValid)
                    {
                        entity.Remove();
                    }
                });
            }
        });

        plugin.RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            RemoveAll();
            return HookResult.Continue;
        });
    }

    private static void RemoveAll()
    {
        foreach (var chicken in Utilities.FindAllEntitiesByDesignerName<CChicken>(Chicken))
        {
            if (chicken.IsValid)
            {
                chicken.Remove();
            }
        }
    }
}
