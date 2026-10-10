using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ServersModes.Shared;

namespace ServersModes.Dm;

// Some bots stand on one spot for minutes with a gun in hand and nobody to
// shoot. A bot that has not moved for a while gets its AI switched back on if
// the game left it off, and one still stuck after that is killed so the game
// respawns it with a fresh AI. What each stuck bot's AI was doing is logged.
public sealed class BotWatch
{
    private const float Interval = 5f;
    private const float Moved = 32f;
    private const float WakeAfter = 20f;
    private const float SlayAfter = 40f;

    private sealed class Watch
    {
        public required Vector Position;
        public float StillSince;
        public bool Reported;
    }

    private readonly ILogger _logger;
    private readonly Dictionary<int, Watch> _watches = new();
    private int _woken;
    private int _slain;

    public BotWatch(BasePlugin plugin)
    {
        _logger = plugin.Logger;
        plugin.AddTimer(Interval, Check, TimerFlags.REPEAT);
        plugin.AddTimer(60f, Report, TimerFlags.REPEAT);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => _watches.Clear());
        plugin.RegisterEventHandler<EventPlayerSpawn>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } player)
            {
                _watches.Remove(player.Slot);
            }

            return HookResult.Continue;
        });
    }

    private void Check()
    {
        // Bots only fight once someone is playing.
        if (!Players.Humans().Any())
        {
            _watches.Clear();
            return;
        }

        var now = Server.CurrentTime;

        foreach (var bot in Utilities.GetPlayers().Where(player => player is { IsValid: true, IsBot: true, IsHLTV: false }))
        {
            if (bot.PlayerPawn.Value is not { LifeState: (byte)LifeState_t.LIFE_ALIVE, AbsOrigin: { } origin } pawn)
            {
                _watches.Remove(bot.Slot);
                continue;
            }

            var position = new Vector(origin.X, origin.Y, origin.Z);

            if (!_watches.TryGetValue(bot.Slot, out var watch) || (watch.Position - position).Length() >= Moved)
            {
                _watches[bot.Slot] = new Watch { Position = position, StillSince = now };
                continue;
            }

            var still = now - watch.StillSince;

            if (still >= SlayAfter)
            {
                _logger.LogWarning("Bot {Name} still stuck after {Seconds:0}s; killing it so it respawns", bot.PlayerName, still);
                _watches.Remove(bot.Slot);
                _slain++;
                pawn.CommitSuicide(false, true);
                continue;
            }

            if (still >= WakeAfter && !watch.Reported)
            {
                watch.Reported = true;
                Wake(bot, pawn, still);
            }
        }
    }

    private void Wake(CCSPlayerController bot, CCSPlayerPawn pawn, float still)
    {
        var ai = pawn.Bot;

        _logger.LogInformation(
            "Bot {Name} has not moved for {Seconds:0}s: pawn.BotAllowActive={PawnActive} ai={HasAi} AllowActive={Active} IsStopping={Stopping} IsAttacking={Attacking} WaitingBehindFriend={Waiting} IsFollowing={Following} IsRogue={Rogue} PathIndex={Path} Goal={Goal} Velocity={Velocity:0} MoveType={MoveType} Weapon={Weapon}",
            bot.PlayerName, still, pawn.BotAllowActive, ai != null,
            ai?.AllowActive, ai?.IsStopping, ai?.IsAttacking, ai?.IsWaitingBehindFriend, ai?.IsFollowing, ai?.IsRogue, ai?.PathIndex,
            ai == null ? null : $"{ai.GoalPosition.X:0},{ai.GoalPosition.Y:0},{ai.GoalPosition.Z:0}",
            pawn.AbsVelocity.Length(), pawn.MoveType, pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName);

        if (!pawn.BotAllowActive)
        {
            pawn.BotAllowActive = true;
            _woken++;
            _logger.LogWarning("Bot {Name}: pawn.BotAllowActive was off, turned it on", bot.PlayerName);
        }

        if (ai is { AllowActive: false })
        {
            ai.AllowActive = true;
            _woken++;
            _logger.LogWarning("Bot {Name}: its AI was not allowed to act, turned it on", bot.PlayerName);
        }
    }

    private void Report()
    {
        if (!Players.Humans().Any())
        {
            return;
        }

        var bots = Utilities.GetPlayers().Count(player => player is { IsValid: true, IsBot: true, IsHLTV: false });
        _logger.LogInformation("Bots on {Map}: {Count}; in the last minute {Woken} woken, {Slain} killed for standing still",
            Server.MapName, bots, _woken, _slain);
        _woken = 0;
        _slain = 0;
    }
}
