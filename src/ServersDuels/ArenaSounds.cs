using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace ServersModes.Duels;

// Arenas sit side by side, so a duellist would hear the next arena's fight.
// Shots (played by each client from the fire-bullets message) and the sound
// events a player or their weapon makes reach only the players of the same
// arena; spectators and the queue still hear everything.
public sealed class ArenaSounds
{
    private const string FireBullets = "CMsgTEFireBullets";
    private const string StartSound = "CMsgSosStartSoundEvent";

    private readonly ILogger _logger;
    private readonly Func<int, int?> _arenaOf;
    private readonly HashSet<string> _reported = new();

    public ArenaSounds(BasePlugin plugin, ILogger logger, Func<int, int?> arenaOf)
    {
        _logger = logger;
        _arenaOf = arenaOf;

        var shots = IdOf(FireBullets, 452);
        var sounds = IdOf(StartSound, 208);
        plugin.HookUserMessage(shots, OnFireBullets, HookMode.Pre);
        plugin.HookUserMessage(sounds, OnStartSound, HookMode.Pre);
        _logger.LogInformation("Arena sounds: hooked {Shots} ({ShotsId}) and {Sounds} ({SoundsId})", FireBullets, shots, StartSound, sounds);
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
        try
        {
            var shooter = new CHandle<CCSPlayerPawn>(message.ReadUInt("player"));

            if (shooter.IsValid)
            {
                Keep(message, FireBullets, SlotOf(shooter.Value));
            }
        }
        catch (Exception error)
        {
            Report($"{FireBullets} failed", error);
        }

        return HookResult.Continue;
    }

    private HookResult OnStartSound(UserMessage message)
    {
        try
        {
            var index = message.ReadInt("source_entity_index");

            if (index > 0)
            {
                Keep(message, StartSound, SlotOf(Utilities.GetEntityFromIndex<CBaseEntity>(index)));
            }
        }
        catch (Exception error)
        {
            Report($"{StartSound} failed", error);
        }

        return HookResult.Continue;
    }

    private void Keep(UserMessage message, string kind, int? source)
    {
        if (source is not int slot || _arenaOf(slot) is not int arena)
        {
            return;
        }

        // Recipients hands out a copy: the kept listeners go back as a new filter.
        var recipients = message.Recipients.ToList();
        var kept = recipients.Where(listener => listener is { IsValid: true } && (_arenaOf(listener.Slot) is not int theirs || theirs == arena)).ToArray();

        if (kept.Length == recipients.Count)
        {
            return;
        }

        message.Recipients = new RecipientFilter(kept);
        Report($"{kind} filtered", null, $"{recipients.Count} -> {kept.Length} listeners, now {message.Recipients.Count}");
    }

    // Once per kind and map load, so the log shows the filter at work without
    // a line per shot.
    private void Report(string what, Exception? error, string detail = "")
    {
        if (!_reported.Add(what))
        {
            return;
        }

        if (error != null)
        {
            _logger.LogError(error, "Arena sounds: {What}", what);
        }
        else
        {
            _logger.LogInformation("Arena sounds: {What}: {Detail}", what, detail);
        }
    }

    public void Reset() => _reported.Clear();

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
