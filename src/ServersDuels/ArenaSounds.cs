using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;

namespace ServersModes.Duels;

// Arenas sit side by side, so a duellist would hear the next arena's fight.
// Shots (played by each client from the fire-bullets message) and the sound
// events a player or their weapon makes reach only the players of the same
// arena; spectators and the queue still hear everything.
public sealed class ArenaSounds
{
    private const string FireBullets = "CMsgTEFireBullets";
    private const string StartSound = "CMsgSosStartSoundEvent";

    private readonly Func<int, int?> _arenaOf;

    public ArenaSounds(BasePlugin plugin, Func<int, int?> arenaOf)
    {
        _arenaOf = arenaOf;
        plugin.HookUserMessage(IdOf(FireBullets, 452), OnFireBullets, HookMode.Pre);
        plugin.HookUserMessage(IdOf(StartSound, 208), OnStartSound, HookMode.Pre);
    }

    private static int IdOf(string name, int fallback)
    {
        try
        {
            var id = UserMessage.FindIdByName(name);
            return id > 0 ? id : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private HookResult OnFireBullets(UserMessage message)
    {
        var shooter = new CHandle<CCSPlayerPawn>(message.ReadUInt("player"));

        if (shooter.IsValid)
        {
            Keep(message, SlotOf(shooter.Value));
        }

        return HookResult.Continue;
    }

    private HookResult OnStartSound(UserMessage message)
    {
        var index = message.ReadInt("source_entity_index");

        if (index > 0)
        {
            Keep(message, SlotOf(Utilities.GetEntityFromIndex<CBaseEntity>(index)));
        }

        return HookResult.Continue;
    }

    private void Keep(UserMessage message, int? source)
    {
        if (source is not int slot || _arenaOf(slot) is not int arena)
        {
            return;
        }

        var deaf = message.Recipients.Where(listener => _arenaOf(listener.Slot) is int theirs && theirs != arena).ToList();

        foreach (var listener in deaf)
        {
            message.Recipients.Remove(listener);
        }
    }

    // The slot of the player behind an entity: their pawn, or a weapon they hold.
    private static int? SlotOf(CBaseEntity? entity)
    {
        if (entity is not { IsValid: true })
        {
            return null;
        }

        if (entity.DesignerName != "player")
        {
            entity = entity.OwnerEntity.Value;

            if (entity is not { IsValid: true, DesignerName: "player" })
            {
                return null;
            }
        }

        var controller = new CCSPlayerPawn(entity.Handle).Controller.Value;
        return controller is { IsValid: true } ? (int)controller.Index - 1 : null;
    }
}
