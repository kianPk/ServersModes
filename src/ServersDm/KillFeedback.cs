using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using ServersModes.Shared;

namespace ServersModes.Dm;

// What a player hears and sees for their own hits and kills: a ding on every
// headshot that does not kill, a kill sound (a heavier one for a headshot),
// and a callout with its own stinger for kills in quick succession. Only
// stock game sounds: a custom one would need every client to download it.
public sealed class KillFeedback
{
    private const float MultiKillWindow = 4f;

    private const string HeadshotHitSound = "sounds/training/bell_normal.vsnd_c";
    private const string KillSound = "sounds/ui/armsrace_kill_01.vsnd_c";
    private const string HeadshotKillSound = "sounds/buttons/bell1.vsnd_c";

    private static readonly (string Name, string Color, string Sound)[] MultiKills =
    [
        ("DOUBLE KILL", "#46a758", "sounds/music/kill_01.vsnd_c"),
        ("TRIPLE KILL", "#3e9bf5", "sounds/music/kill_02.vsnd_c"),
        ("QUADRA KILL", "#a855f7", "sounds/music/kill_03.vsnd_c"),
        ("PENTA KILL", "#f5a524", "sounds/music/kill_bonus.vsnd_c"),
        ("UNSTOPPABLE", "#e5484d", "sounds/music/kill_bonus.vsnd_c"),
    ];

    private const int HitgroupHead = 1;

    private readonly Func<CCSPlayerController, bool> _wantsSounds;
    private readonly Dictionary<int, (int Count, float Last)> _chains = new();

    public KillFeedback(BasePlugin plugin, Func<CCSPlayerController, bool> wantsSounds)
    {
        _wantsSounds = wantsSounds;
        plugin.RegisterEventHandler<EventPlayerHurt>(OnHurt);
        plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
        plugin.RegisterEventHandler<EventPlayerDisconnect>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } player)
            {
                _chains.Remove(player.Slot);
            }

            return HookResult.Continue;
        });
        plugin.RegisterListener<Listeners.OnMapStart>(_ => _chains.Clear());
    }

    private HookResult OnHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;

        if (@event.Health > 0 && @event.Hitgroup == HitgroupHead && Players.IsHuman(attacker) && attacker != @event.Userid)
        {
            Play(attacker!, HeadshotHitSound);
        }

        return HookResult.Continue;
    }

    private HookResult OnDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } victim)
        {
            _chains.Remove(victim.Slot);
        }

        var attacker = @event.Attacker;

        if (!Players.IsHuman(attacker) || attacker == @event.Userid)
        {
            return HookResult.Continue;
        }

        var now = Server.CurrentTime;
        var count = _chains.TryGetValue(attacker!.Slot, out var chain) && now - chain.Last <= MultiKillWindow
            ? chain.Count + 1
            : 1;
        _chains[attacker.Slot] = (count, now);

        if (count < 2)
        {
            Play(attacker, @event.Headshot ? HeadshotKillSound : KillSound);
            return HookResult.Continue;
        }

        var (name, color, sound) = MultiKills[Math.Min(count - 2, MultiKills.Length - 1)];
        Play(attacker, sound);
        attacker.PrintToCenterHtml($"<font class='fontSize-xl' color='{color}'><b>{name}</b></font>", 2);

        if (count >= 4)
        {
            Chat.All($"{ChatColors.Green}{attacker.PlayerName}{ChatColors.Default} got a {ChatColors.Gold}{name}{ChatColors.Default}!");
        }

        return HookResult.Continue;
    }

    private void Play(CCSPlayerController player, string sound)
    {
        if (_wantsSounds(player))
        {
            player.ExecuteClientCommand($"play {sound}");
        }
    }
}
