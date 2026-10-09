using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ServersModes.Shared;

namespace ServersModes.Duels;

public sealed class ServersDuelsPlugin : BasePlugin
{
    public override string ModuleName => "Servers Duels";
    public override string ModuleVersion => "1.0.4";
    public override string ModuleAuthor => "kian";
    public override string ModuleDescription => "1v1 arenas on a ladder, and the Duels map rotation.";

    // Until the pool kept on the site arrives.
    private static readonly ServerMap[] Maps =
    [
        new("am_map", "3626024193"),
        new("Redline NGNW", "3679824083"),
        new("Redline", "3139172262"),
    ];

    private sealed class Duel
    {
        public required int Arena { get; init; }
        public required int T { get; init; }
        public required int? Ct { get; init; }
        public required RoundType Round { get; init; }
        public int? Winner { get; set; }

        public int? Opponent(int slot) => slot == T ? Ct : T;
    }

    private readonly Random _random = new();
    private PreferenceStore _preferences = null!;
    private List<Arena> _arenas = new();
    private bool _arenasFound;
    private readonly ArenaSigns _signs = new();
    private bool _signsLogged;

    // Ladder order: the two players of arena n are at 2n and 2n+1.
    private readonly List<int> _ladder = new();
    private readonly List<int> _queue = new();
    private readonly HashSet<int> _afk = new();
    private readonly Dictionary<int, Duel> _duelOf = new();
    private List<Duel> _duels = new();
    private bool _roundLive;

    public override void Load(bool hotReload)
    {
        Chat.Tag = "Duels";
        _preferences = new PreferenceStore(Path.Combine(ModuleDirectory, "preferences.json"));

        var words = new ChatWords(this);
        _ = new MapVote(this, words, "duels", Maps, 4, MapEnd.Timed);

        words.Add(this, "guns", "Choose your rifle and pistol", (player, _) => OpenGuns(player));
        words.Add(this, "rounds", "Choose the round types you play", (player, _) => OpenRounds(player));
        words.Add(this, "queue", "Your place in the ladder or the queue", (player, _) => ShowQueue(player));
        words.Add(this, "afk", "Step out of the rotation, or come back", (player, _) => ToggleAfk(player));

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterEventHandler<EventPlayerConnectFull>(OnConnect);
        RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        RegisterEventHandler<EventPlayerDeath>(OnDeath);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        AddCommandListener("jointeam", OnJoinTeam);

        if (hotReload)
        {
            foreach (var player in Players.Humans())
            {
                Enqueue(player.Slot);
            }
        }
    }

    private void OnMapStart(string mapName)
    {
        _arenas = new();
        _arenasFound = false;
        _signs.Clear();
        _signsLogged = false;
        _duels = new();
        _duelOf.Clear();
        _roundLive = false;

        // A new map is a new ladder, but the order players had carries over.
        _queue.InsertRange(0, _ladder);
        _ladder.Clear();
    }

    private void EnsureArenas()
    {
        if (_arenasFound)
        {
            return;
        }

        _arenas = ArenaFinder.Find();
        _arenasFound = true;
        Logger.LogInformation("{Map}: found {Count} arenas", Server.MapName, _arenas.Count);
    }

    private static CCSPlayerController? PlayerAt(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        return player is { IsValid: true, IsBot: false, IsHLTV: false }
               && player.Connected == PlayerConnectedState.Connected
            ? player
            : null;
    }

    private void Enqueue(int slot)
    {
        if (!_ladder.Contains(slot) && !_queue.Contains(slot) && !_afk.Contains(slot))
        {
            _queue.Add(slot);
        }
    }

    private void Forget(int slot)
    {
        _ladder.Remove(slot);
        _queue.Remove(slot);
        _afk.Remove(slot);
    }

    private HookResult OnConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (!Players.IsHuman(player))
        {
            return HookResult.Continue;
        }

        Forget(player!.Slot);
        Enqueue(player.Slot);

        AddTimer(5f, () =>
        {
            Chat.To(player, $"Welcome to {ChatColors.Gold}Duels{ChatColors.Default}: win your 1v1 to move up an arena, lose and you move down.");
            Chat.To(player, $"{ChatColors.Green}!guns{ChatColors.Default} weapons · {ChatColors.Green}!rounds{ChatColors.Default} round types · {ChatColors.Green}!queue{ChatColors.Default} · {ChatColors.Green}!afk{ChatColors.Default} · {ChatColors.Green}!rtv");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private HookResult OnDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is not { IsValid: true })
        {
            return HookResult.Continue;
        }

        var slot = player.Slot;
        Forget(slot);

        if (_roundLive && _duelOf.TryGetValue(slot, out var duel) && duel.Winner == null && duel.Opponent(slot) is int opponent)
        {
            duel.Winner = opponent;
            Server.NextFrame(EndRoundIfDecided);
        }

        _duelOf.Remove(slot);
        return HookResult.Continue;
    }

    // Teams are the arenas' business: a player can sit out (spectator) or come
    // back, but not pick a side.
    private HookResult OnJoinTeam(CCSPlayerController? player, CommandInfo info)
    {
        if (!Players.IsHuman(player) || Players.IsWarmup())
        {
            return HookResult.Continue;
        }

        var slot = player!.Slot;
        _ = int.TryParse(info.GetArg(1), out var team);

        if (team == (int)CsTeam.Spectator)
        {
            if (!_afk.Contains(slot))
            {
                SetAfk(player, true);
            }

            return HookResult.Continue;
        }

        if (_afk.Contains(slot))
        {
            SetAfk(player, false);
            return HookResult.Handled;
        }

        if (player.Team == CsTeam.None)
        {
            Enqueue(slot);
            return HookResult.Continue;
        }

        Chat.To(player, $"Arenas pick the teams. Type {ChatColors.Green}!afk{ChatColors.Default} to sit out.");
        return HookResult.Handled;
    }

    private HookResult OnRoundPrestart(EventRoundPrestart @event, GameEventInfo info)
    {
        _duels = new();
        _duelOf.Clear();
        _roundLive = false;

        if (Players.IsWarmup())
        {
            return HookResult.Continue;
        }

        EnsureArenas();

        _ladder.RemoveAll(slot => PlayerAt(slot) == null || _afk.Contains(slot));
        _queue.RemoveAll(slot => PlayerAt(slot) == null || _afk.Contains(slot) || _ladder.Contains(slot));

        foreach (var human in Players.Humans().Where(human => !_afk.Contains(human.Slot)))
        {
            Enqueue(human.Slot);
        }

        if (_arenas.Count == 0)
        {
            return HookResult.Continue;
        }

        var capacity = _arenas.Count * 2;

        while (_ladder.Count < capacity && _queue.Count > 0)
        {
            _ladder.Add(_queue[0]);
            _queue.RemoveAt(0);
        }

        // An odd player out waits at the front of the queue -- unless they are
        // alone, in which case they get an arena to warm up in.
        if (_ladder.Count % 2 == 1 && _ladder.Count > 1)
        {
            _queue.Insert(0, _ladder[^1]);
            _ladder.RemoveAt(_ladder.Count - 1);
        }

        for (var arena = 0; arena * 2 < _ladder.Count; arena++)
        {
            var first = _ladder[arena * 2];
            int? second = arena * 2 + 1 < _ladder.Count ? _ladder[arena * 2 + 1] : null;
            var swap = second != null && _random.Next(2) == 0;

            var duel = new Duel
            {
                Arena = arena,
                T = swap ? second!.Value : first,
                Ct = swap ? first : second,
                Round = PickRound(first, second),
            };

            _duels.Add(duel);
            _duelOf[duel.T] = duel;

            if (duel.Ct is int ct)
            {
                _duelOf[ct] = duel;
            }
        }

        foreach (var duel in _duels)
        {
            Place(duel.T, CsTeam.Terrorist, $"ARENA {duel.Arena + 1}");

            if (duel.Ct is int ct)
            {
                Place(ct, CsTeam.CounterTerrorist, $"ARENA {duel.Arena + 1}");
            }
        }

        foreach (var slot in _queue)
        {
            Place(slot, CsTeam.Spectator, "QUEUE");
        }

        foreach (var slot in _afk)
        {
            Place(slot, CsTeam.Spectator, "AFK");
        }

        _roundLive = true;
        return HookResult.Continue;
    }

    // The round's cleanup may take the signs with it, so they go up again on
    // every round start.
    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        Server.NextFrame(() =>
        {
            EnsureArenas();
            var placed = _signs.Place(_arenas);

            if (!_signsLogged)
            {
                _signsLogged = true;
                Logger.LogInformation("{Map}: placed {Count} signs", Server.MapName, placed);
            }
        });

        return HookResult.Continue;
    }

    private RoundType PickRound(int first, int? second)
    {
        var mine = Preferences(first)?.Rounds ?? Enum.GetValues<RoundType>().ToList();
        var theirs = second is int other ? Preferences(other)?.Rounds ?? mine : mine;
        var shared = mine.Intersect(theirs).ToList();

        if (shared.Count == 0)
        {
            return RoundType.Rifle;
        }

        return shared[_random.Next(shared.Count)];
    }

    private PlayerPreferences? Preferences(int slot) =>
        PlayerAt(slot) is { } player ? _preferences.For(player.SteamID) : null;

    private static void Place(int slot, CsTeam team, string tag)
    {
        var player = PlayerAt(slot);

        if (player == null)
        {
            return;
        }

        if (player.Team != team)
        {
            if (team == CsTeam.Spectator)
            {
                player.ChangeTeam(team);
            }
            else
            {
                player.SwitchTeam(team);
            }
        }

        player.Clan = tag;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
    }

    private HookResult OnSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (!Players.IsHuman(player) || Players.IsWarmup() || _arenas.Count == 0)
        {
            return HookResult.Continue;
        }

        var slot = player!.Slot;

        if (!_duelOf.TryGetValue(slot, out var duel))
        {
            // Spawned outside the ladder (joined mid-freeze): standing on an
            // arena spawn would put a third player in someone's duel.
            if (player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            {
                Server.NextFrame(() =>
                {
                    if (player.IsValid)
                    {
                        player.ChangeTeam(CsTeam.Spectator);
                    }
                });
            }

            return HookResult.Continue;
        }

        AddTimer(0.1f, () => Arm(player, duel), TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private void Arm(CCSPlayerController player, Duel duel)
    {
        var pawn = player.PlayerPawn.Value;

        if (!player.IsValid || pawn is not { LifeState: (byte)LifeState_t.LIFE_ALIVE })
        {
            return;
        }

        var arena = _arenas[duel.Arena % _arenas.Count];
        var isT = duel.T == player.Slot;
        var spots = isT ? arena.T : arena.Ct;
        var spot = spots[0];

        pawn.Teleport(spot.Origin, spot.Angles, new Vector(0, 0, 0));

        var preferences = _preferences.For(player.SteamID);
        player.RemoveWeapons();
        player.GiveNamedItem("weapon_knife");

        switch (duel.Round)
        {
            case RoundType.Rifle:
                player.GiveNamedItem(preferences.Rifle);
                break;
            case RoundType.Awp:
                player.GiveNamedItem("weapon_awp");
                break;
            case RoundType.Scout:
                player.GiveNamedItem("weapon_ssg08");
                break;
        }

        player.GiveNamedItem(preferences.Pistol);
        player.GiveNamedItem(duel.Round == RoundType.Pistol ? "item_kevlar" : "item_assaultsuit");

        var opponent = duel.Opponent(player.Slot) is int other ? PlayerAt(other)?.PlayerName : null;
        var round = Weapons.RoundName(duel.Round);

        Chat.To(
            player,
            opponent == null
                ? $"{ChatColors.Gold}Arena {duel.Arena + 1}{ChatColors.Default} · {round} · waiting for an opponent"
                : $"{ChatColors.Gold}Arena {duel.Arena + 1}{ChatColors.Default} · {round} · vs {ChatColors.LightRed}{opponent}"
        );
        player.PrintToCenter(opponent == null ? $"Arena {duel.Arena + 1} | {round}" : $"Arena {duel.Arena + 1} | {round} | vs {opponent}");
    }

    private HookResult OnDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;

        if (!_roundLive || victim is not { IsValid: true } || !_duelOf.TryGetValue(victim.Slot, out var duel))
        {
            return HookResult.Continue;
        }

        if (duel.Winner == null && duel.Opponent(victim.Slot) is int opponent)
        {
            duel.Winner = opponent;
        }

        Server.NextFrame(EndRoundIfDecided);
        return HookResult.Continue;
    }

    // Each arena ends on its own; the round waits for the last one.
    private void EndRoundIfDecided()
    {
        if (!_roundLive || _duels.Count == 0)
        {
            return;
        }

        var contested = _duels.Where(duel => duel.Ct != null).ToList();

        if (contested.Count == 0 || contested.Any(duel => duel.Winner == null))
        {
            return;
        }

        _roundLive = false;

        var lastWinner = PlayerAt(contested[^1].Winner!.Value);
        var reason = lastWinner?.Team == CsTeam.CounterTerrorist ? RoundEndReason.CTsWin : RoundEndReason.TerroristsWin;
        Players.Rules()?.TerminateRound(3f, reason);
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        _roundLive = false;

        if (Players.IsWarmup() || _duels.Count == 0)
        {
            return HookResult.Continue;
        }

        var winners = new List<int?>();
        var losers = new List<int?>();

        foreach (var duel in _duels.OrderBy(duel => duel.Arena))
        {
            if (duel.Ct is not int ct)
            {
                winners.Add(duel.T);
                losers.Add(null);
                continue;
            }

            var winner = duel.Winner ?? TimeoutWinner(duel.T, ct);
            winners.Add(winner);
            losers.Add(winner == duel.T ? ct : duel.T);
        }

        var count = winners.Count;
        var next = Enumerable.Range(0, count).Select(_ => new List<int>()).ToList();

        for (var arena = 0; arena < count; arena++)
        {
            if (winners[arena] is int winner)
            {
                next[Math.Max(arena - 1, 0)].Add(winner);
            }

            if (losers[arena] is int loser)
            {
                next[Math.Min(arena + 1, count - 1)].Add(loser);
            }
        }

        var ladder = next.SelectMany(pair => pair).Where(slot => PlayerAt(slot) != null && !_afk.Contains(slot)).ToList();

        // Whoever waited plays next: the bottom losers sit out in their place,
        // as many as will not fit next round (the odd one out, or everyone past
        // the last arena). The queue's front fills the ladder at prestart.
        var waiting = _queue.Count(slot => PlayerAt(slot) != null && !_afk.Contains(slot));

        if (waiting > 0)
        {
            var total = ladder.Count + waiting;
            var playing = Math.Min(_arenas.Count * 2, total - total % 2);
            var sitOut = Math.Min(total - playing, waiting);
            var leaving = ladder.Where(slot => losers.Contains(slot)).Reverse().Take(sitOut).ToList();

            foreach (var slot in leaving)
            {
                ladder.Remove(slot);
                _queue.Add(slot);

                if (PlayerAt(slot) is { } player)
                {
                    Chat.To(player, "You sit out the next round so the waiting player can play.");
                }
            }
        }

        _ladder.Clear();
        _ladder.AddRange(ladder);

        for (var arena = 0; arena < count; arena++)
        {
            Tell(winners[arena], true);
            Tell(losers[arena], false);
        }

        return HookResult.Continue;
    }

    // Out of time with both alive: the healthier player takes it, and a tie
    // goes to whoever was higher on the ladder.
    private int TimeoutWinner(int t, int ct)
    {
        var tHealth = Health(t);
        var ctHealth = Health(ct);

        if (tHealth != ctHealth)
        {
            return tHealth > ctHealth ? t : ct;
        }

        var tRank = _ladder.IndexOf(t);
        var ctRank = _ladder.IndexOf(ct);

        if (tRank < 0 || ctRank < 0)
        {
            return tRank < 0 ? ct : t;
        }

        return tRank < ctRank ? t : ct;
    }

    private static int Health(int slot)
    {
        var pawn = PlayerAt(slot)?.PlayerPawn.Value;
        return pawn is { LifeState: (byte)LifeState_t.LIFE_ALIVE } ? pawn.Health : 0;
    }

    private void Tell(int? slot, bool won)
    {
        if (slot is not int value || PlayerAt(value) is not { } player)
        {
            return;
        }

        var index = _ladder.IndexOf(value);
        var where = index >= 0
            ? $"arena {ChatColors.Gold}{index / 2 + 1}{ChatColors.Default} next"
            : "you're in the queue for the next round";

        Chat.To(player, won ? $"{ChatColors.Green}You won{ChatColors.Default} — {where}." : $"{ChatColors.LightRed}You lost{ChatColors.Default} — {where}.");
    }

    private void OpenGuns(CCSPlayerController player)
    {
        var menu = new ChatMenu("Choose your rifle");

        foreach (var rifle in Weapons.Rifles)
        {
            menu.AddMenuOption(rifle.Name, (chooser, _) =>
            {
                _preferences.For(chooser.SteamID).Rifle = rifle.Item;
                _preferences.Save();
                OpenPistols(chooser);
            });
        }

        MenuManager.OpenChatMenu(player, menu);
    }

    private void OpenPistols(CCSPlayerController player)
    {
        var menu = new ChatMenu("Choose your pistol");

        foreach (var pistol in Weapons.Pistols)
        {
            menu.AddMenuOption(pistol.Name, (chooser, _) =>
            {
                var preferences = _preferences.For(chooser.SteamID);
                preferences.Pistol = pistol.Item;
                _preferences.Save();

                var rifle = Weapons.Rifles.FirstOrDefault(entry => entry.Item == preferences.Rifle)?.Name ?? preferences.Rifle;
                Chat.To(chooser, $"Saved: {ChatColors.Gold}{rifle}{ChatColors.Default} + {ChatColors.Gold}{pistol.Name}{ChatColors.Default}, from the next round.");
            });
        }

        MenuManager.OpenChatMenu(player, menu);
    }

    private void OpenRounds(CCSPlayerController player)
    {
        var preferences = _preferences.For(player.SteamID);
        var menu = new ChatMenu("Round types (pick to switch on/off)");

        foreach (var round in Enum.GetValues<RoundType>())
        {
            var on = preferences.Rounds.Contains(round);

            menu.AddMenuOption($"{(on ? "[ON]" : "[OFF]")} {Weapons.RoundName(round)}", (chooser, _) =>
            {
                var mine = _preferences.For(chooser.SteamID);

                if (mine.Rounds.Contains(round))
                {
                    if (mine.Rounds.Count == 1)
                    {
                        Chat.To(chooser, "Keep at least one round type on.");
                    }
                    else
                    {
                        mine.Rounds.Remove(round);
                    }
                }
                else
                {
                    mine.Rounds.Add(round);
                }

                _preferences.Save();
                OpenRounds(chooser);
            });
        }

        MenuManager.OpenChatMenu(player, menu);
    }

    private void ShowQueue(CCSPlayerController player)
    {
        var slot = player.Slot;

        if (_afk.Contains(slot))
        {
            Chat.To(player, $"You're AFK. Type {ChatColors.Green}!afk{ChatColors.Default} to come back.");
            return;
        }

        if (_duelOf.TryGetValue(slot, out var duel))
        {
            Chat.To(player, $"You're in arena {ChatColors.Gold}{duel.Arena + 1}{ChatColors.Default} of {Math.Max(_duels.Count, 1)}.");
            return;
        }

        var place = _queue.IndexOf(slot);

        Chat.To(
            player,
            place >= 0
                ? $"You're {ChatColors.Gold}#{place + 1}{ChatColors.Default} in the queue ({_queue.Count} waiting, {_arenas.Count} arenas)."
                : "You'll get an arena at the start of the next round."
        );
    }

    private void ToggleAfk(CCSPlayerController player) => SetAfk(player, !_afk.Contains(player.Slot));

    private void SetAfk(CCSPlayerController player, bool afk)
    {
        var slot = player.Slot;

        if (afk)
        {
            _afk.Add(slot);
            _queue.Remove(slot);
            Chat.To(player, $"You're AFK and out of the rotation. Type {ChatColors.Green}!afk{ChatColors.Default} to come back.");

            if (!_roundLive || !_duelOf.ContainsKey(slot))
            {
                _ladder.Remove(slot);
                Place(slot, CsTeam.Spectator, "AFK");
            }

            return;
        }

        _afk.Remove(slot);
        Enqueue(slot);
        Chat.To(player, "Welcome back — you'll get an arena at the start of the next round.");
    }
}
