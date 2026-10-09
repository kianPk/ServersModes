using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace ServersModes.Shared;

// A player who presses nothing and says nothing for too long is kicked and
// kept out for a while. Only their own input counts: spawns and the plugin's
// teleports turn a pawn as well, and would keep an idle player in forever.
public sealed class IdleKick
{
    private static readonly TimeSpan Warning = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _idle;
    private readonly TimeSpan _ban;
    private readonly Dictionary<int, (DateTime Active, bool Warned)> _players = new();
    private readonly Dictionary<ulong, DateTime> _banned = new();

    public IdleKick(BasePlugin plugin, TimeSpan idle, TimeSpan ban)
    {
        _idle = idle;
        _ban = ban;

        plugin.RegisterListener<Listeners.OnTick>(OnTick);
        plugin.RegisterEventHandler<EventPlayerConnectFull>(OnConnect);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        plugin.AddCommandListener("say", OnSay);
        plugin.AddCommandListener("say_team", OnSay);
        plugin.AddTimer(1f, Check, TimerFlags.REPEAT);
    }

    private void Active(int slot) => _players[slot] = (DateTime.UtcNow, false);

    private void OnTick()
    {
        foreach (var player in Players.Humans())
        {
            if (player.Pawn.Value?.MovementServices?.Buttons.ButtonStates[0] is > 0)
            {
                Active(player.Slot);
            }
        }
    }

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (Players.IsHuman(player))
        {
            Active(player!.Slot);
        }

        return HookResult.Continue;
    }

    private HookResult OnConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (!Players.IsHuman(player))
        {
            return HookResult.Continue;
        }

        if (_banned.TryGetValue(player!.SteamID, out var until) && until > DateTime.UtcNow)
        {
            var minutes = (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes);
            Server.NextFrame(() => Kick(player, $"Kicked for being AFK. You can rejoin in {minutes} min."));
            return HookResult.Continue;
        }

        _banned.Remove(player.SteamID);
        Active(player.Slot);
        return HookResult.Continue;
    }

    private HookResult OnDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
        {
            _players.Remove(player.Slot);
        }

        return HookResult.Continue;
    }

    private void Check()
    {
        var now = DateTime.UtcNow;

        foreach (var player in Players.Humans().ToList())
        {
            if (!_players.TryGetValue(player.Slot, out var state))
            {
                Active(player.Slot);
                continue;
            }

            var idle = now - state.Active;

            if (idle >= _idle)
            {
                _players.Remove(player.Slot);
                _banned[player.SteamID] = now + _ban;
                Chat.All($"{player.PlayerName} was kicked for being AFK.");
                Kick(player, $"Kicked for being AFK over {(int)_idle.TotalSeconds}s. You can rejoin in {(int)_ban.TotalMinutes} min.");
            }
            else if (idle >= _idle - Warning && !state.Warned)
            {
                _players[player.Slot] = state with { Warned = true };
                Chat.To(player, $"{ChatColors.LightRed}You're AFK{ChatColors.Default}: move within {(int)Warning.TotalSeconds}s or you'll be kicked for {(int)_ban.TotalMinutes} minutes.");
            }
        }
    }

    private static void Kick(CCSPlayerController player, string reason)
    {
        if (player is { IsValid: true, UserId: int userId })
        {
            Server.ExecuteCommand($"kickid {userId} \"{reason}\"");
        }
    }
}
