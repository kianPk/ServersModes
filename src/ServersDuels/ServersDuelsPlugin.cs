using System.Net;
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

// Every arena runs on its own inside one round that never ends: a duel is
// over at the first death, and a couple of seconds later both players are
// free again. Free players are paired by rating, the best pairs on the
// lowest arenas, so nobody waits for the slowest arena and the strong end up
// facing the strong. Whoever has nobody to face yet stands alone in an empty
// arena rather than in spectate.
public sealed class ServersDuelsPlugin : BasePlugin
{
    public override string ModuleName => "Servers Duels";
    public override string ModuleVersion => "1.2.9";
    public override string ModuleAuthor => "kian";
    public override string ModuleDescription => "Independent 1v1 arenas paired by rating, and the Duels map rotation.";

    // Until the pool kept on the site arrives.
    private static readonly ServerMap[] Maps =
    [
        new("am_map", "3626024193"),
        new("Redline NGNW", "3679824083"),
        new("Redline", "3139172262"),
    ];

    // The winner's moment, and time for whoever else finishes to join the
    // next pairing.
    private const float NextDuelAfter = 2f;

    // A pair that just met stays apart this long so that arenas mix instead of
    // replaying the same duel; with nobody else free they meet again anyway.
    private const float RematchAfter = 6f;

    // Out of time with both alive, the healthier player takes it.
    private const float DuelSeconds = 60f;

    // Deathmatch respawns within a couple of seconds; past this it is done by hand.
    private const float RespawnGrace = 5f;

    private const double StartRating = 1000;
    private const double RatingStep = 32;

    private sealed class Duel
    {
        public required int Arena { get; init; }
        public required int T { get; init; }
        public required int Ct { get; init; }
        public required RoundType Round { get; init; }
        public required float Started { get; init; }
        public bool Over { get; set; }

        public int Opponent(int slot) => slot == T ? Ct : T;
    }

    private sealed class Free
    {
        public float Since { get; init; }
        public int? LastOpponent { get; init; }

        // The empty arena they wait in, if they have one.
        public int? Arena { get; set; }

        public float? DeadSince { get; set; }
    }

    private readonly Random _random = new();
    private PreferenceStore _preferences = null!;
    private List<Arena> _arenas = new();
    private bool _arenasFound;
    private readonly ArenaSigns _signs = new();
    private bool _signsLogged;
    private ArenaSounds _sounds = null!;
    private SiteBanner _banner = null!;

    private readonly List<Duel> _duels = new();
    private readonly Dictionary<int, Duel> _duelOf = new();
    private readonly Dictionary<int, Free> _free = new();
    private readonly HashSet<int> _afk = new();
    private bool _live;

    // By SteamID, for as long as the server runs: a map change keeps the order.
    private readonly Dictionary<ulong, double> _ratings = new();

    // Wins between two players on this map, keyed by their SteamIDs in order:
    // each pair keeps its own score, as the teams' score means nothing here.
    private readonly Dictionary<(ulong, ulong), int[]> _scores = new();

    public override void Load(bool hotReload)
    {
        Chat.Tag = "Duels";
        _preferences = new PreferenceStore(Path.Combine(ModuleDirectory, "preferences.json"));

        var words = new ChatWords(this);
        _ = new MapVote(this, words, "duels", Maps, 4, MapEnd.Timed);
        _sounds = new ArenaSounds(this, Logger, ArenaOf);
        _banner = new SiteBanner(this);
        NoHealthshot.Register(this);
        _ = new IdleKick(this, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

        words.Add(this, "guns", "Choose your rifle and pistol", (player, _) => OpenGuns(player));
        words.Add(this, "rounds", "Choose the round types you play", (player, _) => OpenRounds(player));
        words.Add(this, "queue", "Your arena, rating and status", (player, _) => ShowStatus(player));
        words.Add(this, "afk", "Step out of the rotation, or come back", (player, _) => ToggleAfk(player));
        words.Add(this, "invisible", "Report an opponent you can't see", (player, _) => ReportInvisible(player));

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterEventHandler<EventPlayerConnectFull>(OnConnect);
        RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerDeath>(OnDeath);
        RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        AddCommandListener("jointeam", OnJoinTeam);
        AddTimer(1f, Tick, TimerFlags.REPEAT);
        ApplyRules();

        if (hotReload)
        {
            Server.NextFrame(StartArenas);
        }
    }

    // The round is only a frame for the arenas and must not end when a side
    // is wiped out. Deathmatch (the server boots as it, for its HUD) does the
    // spawning: a pawn the game spawns itself always gets its model, where one
    // a plugin puts on a team and respawns can come out invisible. Its random
    // spawns, spawn immunity and bonus weapons stay off. Every arena's Ts (and
    // CTs) are one team, and teammates' names show through walls and on the
    // radar, so as enemies the next arena stays hidden.
    private static void ApplyRules() =>
        Server.ExecuteCommand(string.Join(';', new[]
        {
            "mp_teammates_are_enemies 1",
            "mp_ignore_round_win_conditions 1",
            "mp_roundtime 60",
            "mp_roundtime_defuse 60",
            "mp_roundtime_hostage 60",
            "mp_freezetime 0",
            "mp_respawn_on_death_t 1",
            "mp_respawn_on_death_ct 1",
            "mp_join_grace_time 0",
            "mp_randomspawn 0",
            "mp_respawn_immunitytime -1",
            "mp_dm_bonus_length_max 0",
            "mp_dm_bonus_length_min 0",
            "mp_dm_time_between_bonus_max 9999",
            "mp_dm_time_between_bonus_min 9999",
            "mp_buytime 0",
            "bot_quota 0",
            "bot_quota_mode normal",
            "bot_kick",
        }));

    // The deathmatch game mode's cfg fills the server with bots on every map
    // load, after the rules above may already have run.
    private static void KickBots()
    {
        if (Utilities.GetPlayers().Any(player => player is { IsValid: true, IsBot: true, IsHLTV: false }))
        {
            Server.ExecuteCommand("bot_quota 0;bot_kick");
        }
    }

    // Every spawn outside a duel is the game's, on whichever spawn it picked,
    // maybe on someone's arena: the player is free, and placed at once.
    private HookResult OnSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (Players.IsHuman(player))
        {
            Server.NextFrame(() =>
            {
                if (player!.IsValid && player.PlayerPawn.Value is { IsValid: true } pawn)
                {
                    ClearImmunity(pawn);
                }
            });
        }

        if (!_live || !Players.IsHuman(player) || _duelOf.TryGetValue(player!.Slot, out var live) && !live.Over)
        {
            return HookResult.Continue;
        }

        var slot = player.Slot;

        // A loser back before their duel's pause is up ends it early rather
        // than stand on someone else's arena.
        Server.NextFrame(() =>
        {
            if (_duelOf.TryGetValue(slot, out var over) && over.Over)
            {
                Release(over);
            }

            MakeFree(slot);

            if (_free.TryGetValue(slot, out var free))
            {
                free.Arena = null;
            }

            Match();
        });

        return HookResult.Continue;
    }

    private void OnMapStart(string mapName)
    {
        ApplyRules();
        _arenas = new();
        _arenasFound = false;
        _signs.Clear();
        _signsLogged = false;
        _sounds.Reset();
        _duels.Clear();
        _duelOf.Clear();
        _free.Clear();
        _scores.Clear();
        _live = false;
    }

    private void EnsureArenas()
    {
        if (_arenasFound)
        {
            return;
        }

        _arenas = ArenaFinder.Find();
        _arenasFound = true;
        Logger.LogInformation(
            "{Map}: found {Count} arenas from {T} T and {Ct} CT spawns",
            Server.MapName,
            _arenas.Count,
            _arenas.Sum(arena => arena.T.Count),
            _arenas.Sum(arena => arena.Ct.Count)
        );
    }

    private static CCSPlayerController? PlayerAt(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        return player is { IsValid: true, IsBot: false, IsHLTV: false }
               && player.Connected == PlayerConnectedState.Connected
            ? player
            : null;
    }

    private static bool IsAlive(CCSPlayerController player) =>
        player.PlayerPawn.Value is { LifeState: (byte)LifeState_t.LIFE_ALIVE };

    private int? ArenaOf(int slot)
    {
        if (_duelOf.TryGetValue(slot, out var duel))
        {
            return duel.Arena;
        }

        return _free.TryGetValue(slot, out var free) ? free.Arena : null;
    }

    // Free from the start: no rematch to hold back. A player without a side
    // waits for the game to give them one.
    private void MakeFree(int slot)
    {
        if (!_afk.Contains(slot) && !_duelOf.ContainsKey(slot) && !_free.ContainsKey(slot)
            && PlayerAt(slot) is { Team: CsTeam.Terrorist or CsTeam.CounterTerrorist })
        {
            _free[slot] = new Free { Since = Server.CurrentTime - RematchAfter };
        }
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        Server.NextFrame(StartArenas);
        return HookResult.Continue;
    }

    // A fresh round (the end of warmup, a restart) starts every arena over.
    private void StartArenas()
    {
        _duels.Clear();
        _duelOf.Clear();
        _free.Clear();
        _live = false;

        if (Players.IsWarmup())
        {
            return;
        }

        EnsureArenas();
        ClearTeamScores();
        ApplyRules();

        // The round's cleanup may take the signs with it.
        var placed = _signs.Place(_arenas);

        if (!_signsLogged)
        {
            _signsLogged = true;
            Logger.LogInformation("{Map}: placed {Count} signs", Server.MapName, placed);
        }

        if (_arenas.Count == 0)
        {
            return;
        }

        _live = true;

        foreach (var human in Players.Humans())
        {
            MakeFree(human.Slot);
        }

        Match();
    }

    private void Tick()
    {
        KickBots();

        if (!_live)
        {
            return;
        }

        var now = Server.CurrentTime;

        foreach (var duel in _duels.Where(duel => !duel.Over && now - duel.Started > DuelSeconds).ToList())
        {
            Finish(duel, TimeoutWinner(duel));
        }

        Match();
        ShowScores();
    }

    private double Rating(int slot) =>
        PlayerAt(slot) is { } player && _ratings.TryGetValue(player.SteamID, out var rating) ? rating : StartRating;

    private void Match()
    {
        if (!_live)
        {
            return;
        }

        foreach (var gone in _free.Keys.Where(slot => PlayerAt(slot) == null || _afk.Contains(slot)).ToList())
        {
            _free.Remove(gone);
        }

        // Whoever has waited longest picks first, from those nearest their
        // rating, so an odd player out is never passed over twice.
        var now = Server.CurrentTime;
        var free = _free.Keys.Where(slot => PlayerAt(slot) is { } player && IsAlive(player)).ToList();
        var paired = new HashSet<int>();
        var pairs = new List<(int First, int Second)>();

        foreach (var first in free.OrderBy(slot => _free[slot].Since).ThenByDescending(Rating))
        {
            if (paired.Contains(first))
            {
                continue;
            }

            var others = free.Where(slot => slot != first && !paired.Contains(slot)).ToList();
            var fresh = others.Where(slot => !JustMet(first, slot)).ToList();
            var choices = fresh.Count > 0 ? fresh : others.Where(slot => MayRematch(first, slot, now)).ToList();

            if (choices.Count == 0)
            {
                continue;
            }

            var rating = Rating(first);
            var second = choices.MinBy(slot => Math.Abs(Rating(slot) - rating));
            paired.Add(first);
            paired.Add(second);
            pairs.Add((first, second));
        }

        // The best pairs take the lowest arenas.
        foreach (var (first, second) in pairs.OrderByDescending(pair => Math.Max(Rating(pair.First), Rating(pair.Second))))
        {
            if (FreeArena(first, second) is not int arena)
            {
                break;
            }

            Start(arena, first, second);
        }

        foreach (var slot in _free.Keys.ToList())
        {
            Wait(slot);
        }
    }

    private bool JustMet(int first, int second) =>
        _free[first].LastOpponent == second || _free[second].LastOpponent == first;

    // Two who just met only go again once nobody else is coming free soon.
    private bool MayRematch(int first, int second, float now) =>
        _duels.Count == 0 || now - Math.Max(_free[first].Since, _free[second].Since) >= RematchAfter;

    // The lowest arena nobody fights or waits in, but for the two about to use it.
    private int? FreeArena(params int[] coming)
    {
        var taken = _duels.Select(duel => duel.Arena)
            .Concat(_free.Where(entry => !coming.Contains(entry.Key) && entry.Value.Arena != null).Select(entry => entry.Value.Arena!.Value))
            .ToHashSet();

        for (var arena = 0; arena < _arenas.Count; arena++)
        {
            if (!taken.Contains(arena))
            {
                return arena;
            }
        }

        return null;
    }

    private void Start(int arena, int first, int second)
    {
        _free.Remove(first);
        _free.Remove(second);

        var swap = _random.Next(2) == 0;
        var duel = new Duel
        {
            Arena = arena,
            T = swap ? second : first,
            Ct = swap ? first : second,
            Round = PickRound(first, second),
            Started = Server.CurrentTime,
        };

        _duels.Add(duel);
        _duelOf[first] = duel;
        _duelOf[second] = duel;

        Enter(duel.T, Tag(duel, duel.T), () => Arm(duel, duel.T));
        Enter(duel.Ct, Tag(duel, duel.Ct), () => Arm(duel, duel.Ct));
    }

    // Only living players are paired or placed, so this is just the tag and
    // a moment for the pawn to settle. Teammates are enemies, so a player
    // keeps their side: switching it under a living pawn is never needed.
    private void Enter(int slot, string tag, Action then)
    {
        if (PlayerAt(slot) is not { } player)
        {
            return;
        }

        Tagged(player, tag);
        AddTimer(0.15f, then, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static CsTeam SmallerSide()
    {
        var humans = Players.Humans().ToList();
        var t = humans.Count(player => player.Team == CsTeam.Terrorist);
        var ct = humans.Count(player => player.Team == CsTeam.CounterTerrorist);
        return t <= ct ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
    }

    // Both were alive when paired; one who died since (a fall) is waited for
    // through the game's respawn.
    private void Arm(Duel duel, int slot, int attempt = 0)
    {
        if (duel.Over || !_duelOf.TryGetValue(slot, out var current) || current != duel || PlayerAt(slot) is not { } player)
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;

        if (pawn is not { LifeState: (byte)LifeState_t.LIFE_ALIVE })
        {
            if (attempt < 10)
            {
                AddTimer(0.5f, () => Arm(duel, slot, attempt + 1), TimerFlags.STOP_ON_MAPCHANGE);
            }

            return;
        }

        var arena = _arenas[duel.Arena % _arenas.Count];
        var spot = (slot == duel.T ? arena.T : arena.Ct)[0];
        pawn.Teleport(spot.Origin, spot.Angles, new Vector(0, 0, 0));
        Heal(pawn);

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

        var opponent = PlayerAt(duel.Opponent(slot));
        var (mine, theirs) = Score(slot, duel.Opponent(slot));
        Chat.To(player, $"{ChatColors.Gold}Arena {duel.Arena + 1}{ChatColors.Default} · {Weapons.RoundName(duel.Round)} · vs {ChatColors.LightRed}{opponent?.PlayerName}{ChatColors.Default} · {ChatColors.Green}{mine}{ChatColors.Default}-{ChatColors.LightRed}{theirs}");
    }

    private static void Heal(CCSPlayerPawn pawn)
    {
        pawn.Health = 100;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        ClearImmunity(pawn);
        MakeVisible(pawn);
    }

    private const uint NoDraw = 0x20;

    // Whatever could keep a pawn from being drawn while it still takes hits:
    // the no-draw flag, a faded render, or a model the clients never loaded.
    // Setting the model again has every client load it afresh.
    private static void MakeVisible(CCSPlayerPawn pawn)
    {
        if ((pawn.Effects & NoDraw) != 0)
        {
            pawn.Effects &= ~NoDraw;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_fEffects");
        }

        if (pawn.Render.A != 255 || pawn.RenderMode != RenderMode_t.kRenderNormal)
        {
            pawn.RenderMode = RenderMode_t.kRenderNormal;
            pawn.Render = System.Drawing.Color.FromArgb(255, pawn.Render.R, pawn.Render.G, pawn.Render.B);
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_nRenderMode");
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        }

        var model = ModelOf(pawn);
        pawn.SetModel(string.IsNullOrEmpty(model) ? DefaultModel(pawn) : model);
    }

    private static string? ModelOf(CCSPlayerPawn pawn) =>
        pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName;

    private static string DefaultModel(CCSPlayerPawn pawn) =>
        pawn.TeamNum == (int)CsTeam.CounterTerrorist
            ? "characters/models/ctm_sas/ctm_sas.vmdl"
            : "characters/models/tm_phoenix/tm_phoenix.vmdl";

    private static string Describe(CCSPlayerController? player)
    {
        if (player?.PlayerPawn.Value is not { IsValid: true } pawn)
        {
            return "no pawn";
        }

        var origin = pawn.AbsOrigin;
        return $"{player.PlayerName} team={player.Team} life={pawn.LifeState} hp={pawn.Health} "
               + $"render={pawn.RenderMode}/{pawn.Render.A} effects=0x{pawn.Effects:X} immune={pawn.GunGameImmunity} "
               + $"model='{ModelOf(pawn)}' at=({origin?.X:F0},{origin?.Y:F0},{origin?.Z:F0})";
    }

    // "Someone is invisible", the reporter or their opponent: the state of
    // both goes to the log, and both are redrawn on the spot.
    private void ReportInvisible(CCSPlayerController player)
    {
        var opponent = _duelOf.TryGetValue(player.Slot, out var duel) ? PlayerAt(duel.Opponent(player.Slot)) : null;

        Logger.LogWarning(
            "Invisible report in arena {Arena}: viewer {Viewer} | opponent {Opponent}",
            duel is null ? "none" : duel.Arena + 1,
            Describe(player),
            opponent is null ? "none" : Describe(opponent)
        );

        foreach (var redrawn in new[] { player, opponent })
        {
            if (redrawn?.PlayerPawn.Value is { IsValid: true, LifeState: (byte)LifeState_t.LIFE_ALIVE } pawn)
            {
                MakeVisible(pawn);
            }
        }

        Chat.To(player, "Thanks — reported, and redrawn.");
    }

    // Deathmatch's spawn immunity (the INVULNERABLE box), should a spawn have
    // granted it despite mp_respawn_immunitytime.
    private static void ClearImmunity(CCSPlayerPawn pawn)
    {
        if (pawn.GunGameImmunity)
        {
            pawn.GunGameImmunity = false;
            pawn.ImmuneToGunGameDamageTime = 0;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_bGunGameImmunity");
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_fImmuneToGunGameDamageTime");
        }
    }

    // Alone in an empty arena until someone comes free; where the game
    // spawned them when every arena is taken. The dead wait for the game's
    // respawn, which places them, and are only respawned by hand should it
    // never come.
    private void Wait(int slot)
    {
        if (!_free.TryGetValue(slot, out var free) || PlayerAt(slot) is not { } player)
        {
            return;
        }

        if (!IsAlive(player))
        {
            var now = Server.CurrentTime;
            free.DeadSince ??= now;

            if (now - free.DeadSince >= RespawnGrace && player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            {
                free.DeadSince = now;
                player.Respawn();
            }

            return;
        }

        free.DeadSince = null;

        var inDuelArena = free.Arena is int held && _duels.Any(duel => duel.Arena == held);

        if (free.Arena != null && !inDuelArena && IsAlive(player))
        {
            return;
        }

        if (inDuelArena)
        {
            free.Arena = null;
        }

        if (free.Arena == null)
        {
            free.Arena = FreeArena(slot);
        }

        if (free.Arena is not int arena)
        {
            Tagged(player, "QUEUE");
            return;
        }

        Enter(slot, $"ARENA {arena + 1}", () =>
        {
            if (!_free.TryGetValue(slot, out var still) || still.Arena != arena || PlayerAt(slot) is not { } waiting)
            {
                return;
            }

            var pawn = waiting.PlayerPawn.Value;

            if (pawn is not { LifeState: (byte)LifeState_t.LIFE_ALIVE })
            {
                return;
            }

            var spot = _arenas[arena].T[0];
            pawn.Teleport(spot.Origin, spot.Angles, new Vector(0, 0, 0));
            Heal(pawn);
            waiting.RemoveWeapons();
            waiting.GiveNamedItem("weapon_knife");
        });
    }

    private static void Tagged(CCSPlayerController player, string tag)
    {
        player.Clan = tag;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
    }

    private HookResult OnDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;

        if (_live && victim is { IsValid: true } && _duelOf.TryGetValue(victim.Slot, out var duel) && !duel.Over)
        {
            Finish(duel, duel.Opponent(victim.Slot));
        }

        return HookResult.Continue;
    }

    // A null winner is a draw: out of time on equal health.
    private void Finish(Duel duel, int? winner)
    {
        if (duel.Over)
        {
            return;
        }

        duel.Over = true;

        if (winner is int won)
        {
            var lost = duel.Opponent(won);
            AddWin(won, lost);
            var change = Rate(won, lost);
            Tell(won, lost, true, change);
            Tell(lost, won, false, -change);
        }
        else
        {
            foreach (var slot in new[] { duel.T, duel.Ct })
            {
                if (PlayerAt(slot) is { } player)
                {
                    Chat.To(player, "Out of time on equal health — a draw.");
                }
            }
        }

        AddTimer(NextDuelAfter, () => Release(duel), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void Release(Duel duel)
    {
        if (!_duels.Remove(duel))
        {
            return;
        }

        var now = Server.CurrentTime;

        foreach (var slot in new[] { duel.T, duel.Ct })
        {
            if (_duelOf.TryGetValue(slot, out var current) && current == duel)
            {
                _duelOf.Remove(slot);
            }

            if (PlayerAt(slot) is { } player && !_afk.Contains(slot))
            {
                // A survivor waits where they stand.
                _free[slot] = new Free
                {
                    Since = now,
                    LastOpponent = duel.Opponent(slot),
                    Arena = IsAlive(player) ? duel.Arena : null,
                };
            }
        }

        Match();
    }

    private int? TimeoutWinner(Duel duel)
    {
        var tHealth = Health(duel.T);
        var ctHealth = Health(duel.Ct);

        if (tHealth == ctHealth)
        {
            return null;
        }

        return tHealth > ctHealth ? duel.T : duel.Ct;
    }

    private static int Health(int slot)
    {
        var pawn = PlayerAt(slot)?.PlayerPawn.Value;
        return pawn is { LifeState: (byte)LifeState_t.LIFE_ALIVE } ? pawn.Health : 0;
    }

    // Elo: beating a stronger player moves both further than beating a weaker one.
    private int Rate(int winner, int loser)
    {
        if (PlayerAt(winner) is not { } won || PlayerAt(loser) is not { } lost)
        {
            return 0;
        }

        var winnerRating = Rating(winner);
        var loserRating = Rating(loser);
        var expected = 1 / (1 + Math.Pow(10, (loserRating - winnerRating) / 400));
        var change = RatingStep * (1 - expected);

        _ratings[won.SteamID] = winnerRating + change;
        _ratings[lost.SteamID] = loserRating - change;
        return (int)Math.Round(change);
    }

    private void Tell(int slot, int opponent, bool won, int change)
    {
        if (PlayerAt(slot) is not { } player)
        {
            return;
        }

        var (mine, theirs) = Score(slot, opponent);
        var name = PlayerAt(opponent)?.PlayerName ?? "your opponent";
        var rating = $"rating {(int)Math.Round(Rating(slot))} ({(change >= 0 ? "+" : "")}{change})";

        Chat.To(
            player,
            won
                ? $"{ChatColors.Green}You won{ChatColors.Default} vs {name} ({mine}-{theirs}) — {rating}."
                : $"{ChatColors.LightRed}You lost{ChatColors.Default} vs {name} ({mine}-{theirs}) — {rating}."
        );
    }

    private HookResult OnConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (!Players.IsHuman(player))
        {
            return HookResult.Continue;
        }

        Forget(player!.Slot);

        AddTimer(5f, () =>
        {
            Chat.To(player, $"Welcome to {ChatColors.Gold}Duels{ChatColors.Default}: every kill brings your next opponent right away, matched to your level.");
            Chat.To(player, $"{ChatColors.Green}!guns{ChatColors.Default} weapons · {ChatColors.Green}!rounds{ChatColors.Default} round types · {ChatColors.Green}!queue{ChatColors.Default} · {ChatColors.Green}!afk{ChatColors.Default} · {ChatColors.Green}!rtv");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private HookResult OnDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
        {
            Forget(player.Slot);
        }

        return HookResult.Continue;
    }

    private void Forget(int slot)
    {
        _free.Remove(slot);
        _afk.Remove(slot);
        Forfeit(slot);
    }

    private void Forfeit(int slot)
    {
        if (_duelOf.TryGetValue(slot, out var duel) && !duel.Over)
        {
            Finish(duel, duel.Opponent(slot));
        }
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

        // A newcomer's first side is the game's to give, with its spawn.
        if (player.Team == CsTeam.None)
        {
            return HookResult.Continue;
        }

        Chat.To(player, $"Arenas pick the teams. Type {ChatColors.Green}!afk{ChatColors.Default} to sit out.");
        return HookResult.Handled;
    }

    private RoundType PickRound(int first, int second)
    {
        var mine = Preferences(first)?.Rounds ?? Enum.GetValues<RoundType>().ToList();
        var theirs = Preferences(second)?.Rounds ?? mine;
        var shared = mine.Intersect(theirs).ToList();

        if (shared.Count == 0)
        {
            return RoundType.Rifle;
        }

        return shared[_random.Next(shared.Count)];
    }

    private PlayerPreferences? Preferences(int slot) =>
        PlayerAt(slot) is { } player ? _preferences.For(player.SteamID) : null;

    private string Tag(Duel duel, int slot)
    {
        var (mine, theirs) = Score(slot, duel.Opponent(slot));
        return $"ARENA {duel.Arena + 1} | {mine}-{theirs}";
    }

    private static (ulong, ulong)? PairOf(int slot, int opponent)
    {
        if (PlayerAt(slot) is not { } me || PlayerAt(opponent) is not { } them)
        {
            return null;
        }

        return me.SteamID < them.SteamID ? (me.SteamID, them.SteamID) : (them.SteamID, me.SteamID);
    }

    private (int Mine, int Theirs) Score(int slot, int opponent)
    {
        if (PairOf(slot, opponent) is not { } pair || !_scores.TryGetValue(pair, out var wins))
        {
            return (0, 0);
        }

        return PlayerAt(slot)!.SteamID == pair.Item1 ? (wins[0], wins[1]) : (wins[1], wins[0]);
    }

    private void AddWin(int winner, int loser)
    {
        if (PairOf(winner, loser) is not { } pair)
        {
            return;
        }

        if (!_scores.TryGetValue(pair, out var wins))
        {
            _scores[pair] = wins = new int[2];
        }

        wins[PlayerAt(winner)!.SteamID == pair.Item1 ? 0 : 1]++;
    }

    // The teams' score is the same for everyone and means nothing here.
    private static void ClearTeamScores()
    {
        foreach (var team in Utilities.FindAllEntitiesByDesignerName<CTeam>("cs_team_manager"))
        {
            if (team.Score != 0)
            {
                team.Score = 0;
                Utilities.SetStateChanged(team, "CTeam", "m_iScore");
            }
        }
    }

    // The teams' score at the top is the same for everyone, so each duellist
    // gets their own: arena, round and the score against their opponent.
    private void ShowScores()
    {
        foreach (var duel in _duels)
        {
            ShowScore(duel, duel.T);
            ShowScore(duel, duel.Ct);
        }

        foreach (var (slot, free) in _free)
        {
            if (!_banner.Showing(slot) && PlayerAt(slot) is { } player)
            {
                var where = free.Arena is int arena ? $"<font color='#f5a524'>ARENA {arena + 1}</font> · " : "";
                player.PrintToCenterHtml($"{where}Finding your next opponent…<br>Rating {(int)Math.Round(Rating(slot))}", 2);
            }
        }
    }

    private void ShowScore(Duel duel, int slot)
    {
        if (_banner.Showing(slot) || PlayerAt(slot) is not { } player)
        {
            return;
        }

        var other = duel.Opponent(slot);
        var (mine, theirs) = Score(slot, other);
        var name = WebUtility.HtmlEncode(PlayerAt(other)?.PlayerName ?? "");
        player.PrintToCenterHtml(
            $"<font color='#f5a524'>ARENA {duel.Arena + 1}</font> · {Weapons.RoundName(duel.Round)}<br><font color='#5ee35e'>YOU {mine}</font> : <font color='#ff6b6b'>{theirs} {name}</font>",
            2
        );
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
                Chat.To(chooser, $"Saved: {ChatColors.Gold}{rifle}{ChatColors.Default} + {ChatColors.Gold}{pistol.Name}{ChatColors.Default}, from your next duel.");
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

    private void ShowStatus(CCSPlayerController player)
    {
        var slot = player.Slot;
        var rating = (int)Math.Round(Rating(slot));

        if (_afk.Contains(slot))
        {
            Chat.To(player, $"You're AFK. Type {ChatColors.Green}!afk{ChatColors.Default} to come back.");
            return;
        }

        if (_duelOf.TryGetValue(slot, out var duel))
        {
            Chat.To(player, $"You're in arena {ChatColors.Gold}{duel.Arena + 1}{ChatColors.Default} of {_arenas.Count} · rating {ChatColors.Gold}{rating}");
            return;
        }

        Chat.To(player, $"Finding your next opponent · rating {ChatColors.Gold}{rating}{ChatColors.Default} · {_free.Count} free, {_duels.Count} duels running.");
    }

    private void ToggleAfk(CCSPlayerController player) => SetAfk(player, !_afk.Contains(player.Slot));

    private void SetAfk(CCSPlayerController player, bool afk)
    {
        var slot = player.Slot;

        if (afk)
        {
            _afk.Add(slot);
            _free.Remove(slot);
            Forfeit(slot);

            if (player.Team != CsTeam.Spectator)
            {
                player.ChangeTeam(CsTeam.Spectator);
            }

            Tagged(player, "AFK");
            Chat.To(player, $"You're out of the rotation. Type {ChatColors.Green}!afk{ChatColors.Default} to come back — a minute without input still gets you kicked.");
            return;
        }

        _afk.Remove(slot);

        if (player.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
        {
            player.ChangeTeam(SmallerSide());
        }

        MakeFree(slot);
        Chat.To(player, "Welcome back — your next opponent is on the way.");
    }
}
