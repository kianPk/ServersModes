using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ServersModes.Shared;

namespace ServersModes.Bhop;

// Bunny hop the way xplay runs it: auto-bhop, no stamina, everyone on one side
// passing through each other, and a timer on the map's own zones. CS2 bhop
// maps mark them with named triggers (timer_startzone / timer_endzone, and
// map_start / map_end or s1_start on older ports); leaving the start starts
// the clock, touching the end stops it. Bonuses work the same way per number.
public sealed partial class ServersBhopPlugin : BasePlugin
{
    public override string ModuleName => "Servers BHOP";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "kian";
    public override string ModuleDescription => "Bunny hop with a timer, records and the map rotation.";

    private static readonly ServerMap[] Maps =
    [
        new("bhop_emevaelx3", "3070665979"),
        new("bhop_dust_temple", "3738689616"),
        new("bhop_rc_nuclear", "3162105298"),
        new("bhop_colour", "3071726325"),
        new("bhop_cherryblossom", "3082038560"),
        new("bhop_bug100_2nd", "3721089159"),
        new("bhop_skylook2", "3225291020"),
        new("bhop_winterland", "3658482686"),
        new("bhop_treehouse2", "3659295388"),
        new("bhop_easyjump_daily2", "3694996467"),
        new("bhop_alt_vaahtera", "3459040844"),
        new("bhop_quaker", "3647662024"),
    ];

    // Leaving the start faster than this would be a head start.
    private const float MaxStartSpeed = 290f;
    private const int HudEveryTicks = 8;

    private enum ZoneKind
    {
        Start,
        End,
    }

    private sealed record Zone(ZoneKind Kind, int Bonus);

    private sealed record Saved(Vector Origin, QAngle Angles, Vector Velocity, int Track, float? Elapsed);

    private sealed class Run
    {
        public int Track;
        public float? Started;
        public bool InStart;
        public bool Practice;
        public Saved? Saved;
        public bool SmallHud;
        public int Fov;
    }

    private RecordStore _records = null!;
    private SiteBanner _banner = null!;
    private readonly Dictionary<int, Run> _runs = new();
    private bool? _hasZones;
    private int _ticks;

    public override void Load(bool hotReload)
    {
        Chat.Tag = "BHOP";
        _records = new RecordStore(Path.Combine(ModuleDirectory, "records.json"));

        var words = new ChatWords(this);
        _ = new MapVote(this, words, "bhop", Maps, 5, MapEnd.Timed);
        _banner = new SiteBanner(this);
        NoHealthshot.Register(this);
        NoBots.Register(this);

        foreach (var word in new[] { "r", "restart", "spawn", "main", "start" })
        {
            words.Add(this, word, "Back to the start of the map", (player, _) => Restart(player));
        }

        words.Add(this, "b", "Go to bonus N", (player, args) => GoToBonus(player, args));
        words.Add(this, "bonus", "Go to bonus N", (player, args) => GoToBonus(player, args));
        words.Add(this, "usp", "Get a USP-S", (player, _) => Give(player, "weapon_usp_silencer"));
        words.Add(this, "glock", "Get a Glock-18", (player, _) => Give(player, "weapon_glock"));
        words.Add(this, "knife", "Get a knife", (player, _) => Give(player, "weapon_knife"));
        words.Add(this, "spec", "Watch instead of playing", (player, _) => player.ChangeTeam(CsTeam.Spectator));
        words.Add(this, "fov", "Change your field of view (1-140, default 90)", SetFov);
        words.Add(this, "wr", "The map's record", (player, _) => ShowRecord(player, 0));
        words.Add(this, "bwr", "A bonus's record", (player, args) => ShowRecord(player, BonusNumber(args)));
        words.Add(this, "pb", "Your best time on this map", (player, _) => ShowPersonalBest(player));
        words.Add(this, "top", "The map's ten best times", (player, _) => ShowTop(player));
        words.Add(this, "info", "Map name, record and your best", (player, _) => ShowInfo(player));
        words.Add(this, "practice", "Pause the timer and move freely", (player, _) => EnterPractice(player, noclip: false));
        words.Add(this, "noclip", "Fly through walls (practice mode)", (player, _) => ToggleNoclip(player));
        words.Add(this, "resume", "Leave practice and carry on where you were", (player, _) => Resume(player));
        words.Add(this, "hud_s", "Smaller timer", (player, _) => RunOf(player.Slot).SmallHud = true);
        words.Add(this, "hud_m", "Normal timer", (player, _) => RunOf(player.Slot).SmallHud = false);

        HookEntityOutput("trigger_multiple", "OnStartTouch", OnStartTouch);
        HookEntityOutput("trigger_multiple", "OnEndTouch", OnEndTouch);

        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _runs.Clear();
            _hasZones = null;
            ApplyRules();
        });
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            ApplyRules();
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        RegisterEventHandler<EventPlayerDeath>(OnDeath);
        RegisterEventHandler<EventPlayerConnectFull>(OnConnect);
        RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        ApplyRules();
    }

    // The game mode's own cfg runs on every map load and may undo the api's
    // cfg; these are the rules the mode depends on.
    private static void ApplyRules() =>
        Server.ExecuteCommand(string.Join(';', new[]
        {
            "sv_enablebunnyhopping 1",
            "sv_autobunnyhopping 1",
            "sv_airaccelerate 1000",
            "sv_staminamax 0",
            "sv_staminajumpcost 0",
            "sv_staminalandcost 0",
            "sv_staminarecoveryrate 0",
            "sv_accelerate_use_weapon_speed 0",
            "sv_falldamage_scale 0",
            "mp_humanteam CT",
            "mp_solid_teammates 0",
            "mp_friendlyfire 0",
            "mp_respawn_on_death_ct 1",
            "mp_respawn_on_death_t 1",
            "mp_ignore_round_win_conditions 1",
        }));

    private Run RunOf(int slot)
    {
        if (!_runs.TryGetValue(slot, out var run))
        {
            run = new Run();
            _runs[slot] = run;
        }

        return run;
    }

    [GeneratedRegex(@"^(?:timer_startzone|timer_start|map_start|s1_start|stage1_start|start_zone|zone_start)$", RegexOptions.IgnoreCase)]
    private static partial Regex StartName();

    [GeneratedRegex(@"^(?:timer_endzone|timer_end|map_end|end_zone|zone_end)$", RegexOptions.IgnoreCase)]
    private static partial Regex EndName();

    [GeneratedRegex(@"^(?:timer_bonus(\d+)_startzone|b(\d+)_start|bonus(\d+)_start)$", RegexOptions.IgnoreCase)]
    private static partial Regex BonusStartName();

    [GeneratedRegex(@"^(?:timer_bonus(\d+)_endzone|b(\d+)_end|bonus(\d+)_end)$", RegexOptions.IgnoreCase)]
    private static partial Regex BonusEndName();

    private static Zone? ZoneOf(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (StartName().IsMatch(name))
        {
            return new Zone(ZoneKind.Start, 0);
        }

        if (EndName().IsMatch(name))
        {
            return new Zone(ZoneKind.End, 0);
        }

        if (BonusStartName().Match(name) is { Success: true } start)
        {
            return new Zone(ZoneKind.Start, BonusOf(start));
        }

        return BonusEndName().Match(name) is { Success: true } end ? new Zone(ZoneKind.End, BonusOf(end)) : null;
    }

    private static int BonusOf(Match match) =>
        int.Parse(match.Groups.Values.Skip(1).First(group => group.Success).Value);

    private static IEnumerable<(CBaseEntity Trigger, Zone Zone)> Zones() =>
        Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("trigger_multiple")
            .Select(trigger => (trigger, ZoneOf(trigger.Entity?.Name)))
            .Where(entry => entry.Item2 != null)
            .Select(entry => (entry.trigger, entry.Item2!));

    private bool HasZones()
    {
        if (_hasZones == null)
        {
            var zones = Zones().ToList();
            _hasZones = zones.Any(entry => entry.Zone is { Kind: ZoneKind.Start, Bonus: 0 })
                        && zones.Any(entry => entry.Zone is { Kind: ZoneKind.End, Bonus: 0 });
            Logger.LogInformation("{Map}: {Count} timer zones ({Names})", Server.MapName, zones.Count,
                string.Join(", ", zones.Select(entry => entry.Trigger.Entity?.Name)));
        }

        return _hasZones.Value;
    }

    private static CCSPlayerController? PlayerOf(CEntityInstance? activator)
    {
        if (activator is not { IsValid: true } || activator.DesignerName != "player")
        {
            return null;
        }

        var player = new CCSPlayerPawn(activator.Handle).OriginalController.Value;
        return Players.IsHuman(player) ? player : null;
    }

    private HookResult OnStartTouch(CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)
    {
        if (ZoneOf(caller.Entity?.Name) is not { } zone || PlayerOf(activator) is not { } player)
        {
            return HookResult.Continue;
        }

        var run = RunOf(player.Slot);

        if (run.Practice)
        {
            return HookResult.Continue;
        }

        if (zone.Kind == ZoneKind.Start)
        {
            run.InStart = true;
            run.Track = zone.Bonus;
            run.Started = null;
        }
        else if (run.Track == zone.Bonus && run.Started is { } started)
        {
            Finished(player, run, Server.CurrentTime - started);
        }

        return HookResult.Continue;
    }

    private HookResult OnEndTouch(CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)
    {
        if (ZoneOf(caller.Entity?.Name) is not { Kind: ZoneKind.Start } zone || PlayerOf(activator) is not { } player)
        {
            return HookResult.Continue;
        }

        var run = RunOf(player.Slot);

        if (run.Practice || run.Track != zone.Bonus)
        {
            return HookResult.Continue;
        }

        run.InStart = false;

        if (player.PlayerPawn.Value is { LifeState: (byte)LifeState_t.LIFE_ALIVE } pawn)
        {
            CapStartSpeed(pawn);
        }

        run.Started = Server.CurrentTime;
        return HookResult.Continue;
    }

    private static void CapStartSpeed(CCSPlayerPawn pawn)
    {
        var velocity = pawn.AbsVelocity;
        var speed = velocity.Length2D();

        if (speed <= MaxStartSpeed)
        {
            return;
        }

        var scale = MaxStartSpeed / speed;
        pawn.Teleport(null, null, new Vector(velocity.X * scale, velocity.Y * scale, velocity.Z));
    }

    private void Finished(CCSPlayerController player, Run run, double time)
    {
        var map = Server.MapName;
        var track = RecordStore.Track(run.Track);
        var where = run.Track == 0 ? map : $"{map} bonus {run.Track}";
        var previous = _records.Best(map, track, player.SteamID)?.Time;
        var result = _records.Submit(map, track, player.SteamID, player.PlayerName, time);
        run.Started = null;

        var shown = $"{ChatColors.Gold}{RecordStore.Format(time)}{ChatColors.Default}";

        switch (result)
        {
            case Finish.WorldRecord:
                Chat.All($"{ChatColors.Green}{player.PlayerName}{ChatColors.Default} set the {ChatColors.Gold}server record{ChatColors.Default} on {where}: {shown}!");
                break;
            case Finish.PersonalBest:
                var gain = previous is { } before ? $" (-{RecordStore.Format(before - time)})" : "";
                Chat.All($"{ChatColors.Green}{player.PlayerName}{ChatColors.Default} finished {where} in {shown}{gain} · rank {_records.Rank(map, track, player.SteamID)}/{_records.Count(map, track)}");
                break;
            default:
                Chat.To(player, $"You finished {where} in {shown} (+{RecordStore.Format(time - previous!.Value)} on your best).");
                break;
        }
    }

    private void OnTick()
    {
        if (++_ticks % HudEveryTicks != 0)
        {
            return;
        }

        var players = Players.Humans().ToList();

        // Nobody joins before the map's entities exist, so the zones are
        // looked up only once someone is here to see them.
        if (players.Count == 0)
        {
            return;
        }

        var now = Server.CurrentTime;
        var zones = HasZones();

        foreach (var player in players)
        {
            if (_banner.Showing(player.Slot) || player.PlayerPawn.Value is not { LifeState: (byte)LifeState_t.LIFE_ALIVE } pawn)
            {
                continue;
            }

            var run = RunOf(player.Slot);
            var speed = $"<font color='#cbd2d9'>{(int)pawn.AbsVelocity.Length2D()} u/s</font>";
            var size = run.SmallHud ? "fontSize-s" : "fontSize-l";
            var bonus = run.Track > 0 ? $" <font color='#9aa0a6'>B{run.Track}</font>" : "";

            var line = !zones ? ""
                : run.Practice ? "<font color='#f5a524'>Practice</font> <font color='#9aa0a6'>· !resume</font>"
                : run.Started is { } started ? $"<font color='#e3d39a'>{RecordStore.Format(now - started)}</font>{bonus}"
                : run.InStart ? $"<font color='#46a758'>Start zone</font>{bonus}"
                : "<font color='#9aa0a6'>Timer stopped · !r</font>";

            player.PrintToCenterHtml(line.Length > 0 ? $"<span class='{size}'>{line}</span><br>{speed}" : speed, 1);
        }
    }

    private HookResult OnSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (!Players.IsHuman(player))
        {
            return HookResult.Continue;
        }

        var run = RunOf(player!.Slot);
        run.Started = null;
        run.Practice = false;
        run.Saved = null;

        if (run.Fov > 0)
        {
            AddTimer(0.2f, () => ApplyFov(player, run.Fov), TimerFlags.STOP_ON_MAPCHANGE);
        }

        return HookResult.Continue;
    }

    private HookResult OnDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (Players.IsHuman(@event.Userid))
        {
            var run = RunOf(@event.Userid!.Slot);
            run.Started = null;
            run.Practice = false;
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

        AddTimer(1f, () =>
        {
            if (player!.IsValid && player.Team is CsTeam.None or CsTeam.Spectator)
            {
                player.ChangeTeam(CsTeam.CounterTerrorist);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);

        AddTimer(5f, () =>
        {
            if (!player!.IsValid)
            {
                return;
            }

            Chat.To(player, $"Welcome to {ChatColors.Gold}BHOP{ChatColors.Default}: auto-bhop on, leave the start zone to start the timer.");
            Chat.To(player, $"{ChatColors.Green}!r{ChatColors.Default} restart · {ChatColors.Green}!wr !pb !top{ChatColors.Default} records · {ChatColors.Green}!practice !noclip !resume{ChatColors.Default} · {ChatColors.Green}!rtv !nominate");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private HookResult OnDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
        {
            _runs.Remove(player.Slot);
        }

        return HookResult.Continue;
    }

    private static CCSPlayerPawn? AlivePawn(CCSPlayerController player) =>
        player.PlayerPawn.Value is { LifeState: (byte)LifeState_t.LIFE_ALIVE } pawn ? pawn : null;

    private static (Vector Origin, QAngle Angles)? SpawnSpot()
    {
        var spawn = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_counterterrorist").FirstOrDefault()
                    ?? Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_terrorist").FirstOrDefault();

        return spawn is { AbsOrigin: { } origin, AbsRotation: { } angles }
            ? (new Vector(origin.X, origin.Y, origin.Z), new QAngle(angles.X, angles.Y, angles.Z))
            : null;
    }

    private void Restart(CCSPlayerController player)
    {
        if (player.Team != CsTeam.CounterTerrorist)
        {
            player.ChangeTeam(CsTeam.CounterTerrorist);
            return;
        }

        if (AlivePawn(player) is not { } pawn)
        {
            player.Respawn();
            return;
        }

        LeavePractice(pawn, RunOf(player.Slot));

        if (SpawnSpot() is { } spot)
        {
            pawn.Teleport(spot.Origin, spot.Angles, new Vector(0, 0, 0));
        }

        var run = RunOf(player.Slot);
        run.Track = 0;
        run.Started = null;
    }

    private static int BonusNumber(string[] args) =>
        args.Length > 0 && int.TryParse(args[0], out var number) && number > 0 ? number : 1;

    private void GoToBonus(CCSPlayerController player, string[] args)
    {
        var number = BonusNumber(args);

        if (AlivePawn(player) is not { } pawn)
        {
            return;
        }

        var start = Zones().FirstOrDefault(entry => entry.Zone is { Kind: ZoneKind.Start } && entry.Zone.Bonus == number).Trigger;

        if (start is not { AbsOrigin: { } origin, Collision: { } collision })
        {
            Chat.To(player, $"This map has no bonus {number}.");
            return;
        }

        var run = RunOf(player.Slot);
        LeavePractice(pawn, run);

        var mins = collision.Mins;
        var maxs = collision.Maxs;
        var spot = new Vector(origin.X + (mins.X + maxs.X) / 2, origin.Y + (mins.Y + maxs.Y) / 2, origin.Z + mins.Z + 8);
        pawn.Teleport(spot, null, new Vector(0, 0, 0));

        run.Track = number;
        run.Started = null;
    }

    private static void Give(CCSPlayerController player, string item)
    {
        if (AlivePawn(player) == null)
        {
            return;
        }

        if (item != "weapon_knife")
        {
            player.RemoveItemByDesignerName("weapon_usp_silencer");
            player.RemoveItemByDesignerName("weapon_glock");
        }

        player.RemoveItemByDesignerName(item);
        player.GiveNamedItem(item);
    }

    private void SetFov(CCSPlayerController player, string[] args)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out var fov) || fov is < 1 or > 140)
        {
            Chat.To(player, "Usage: !fov 1-140 (default 90)");
            return;
        }

        RunOf(player.Slot).Fov = fov;
        ApplyFov(player, fov);
        Chat.To(player, $"Field of view: {ChatColors.Gold}{fov}");
    }

    private static void ApplyFov(CCSPlayerController player, int fov)
    {
        if (!player.IsValid)
        {
            return;
        }

        player.DesiredFOV = (uint)fov;
        Utilities.SetStateChanged(player, "CBasePlayerController", "m_iDesiredFOV");
    }

    private void ShowRecord(CCSPlayerController player, int bonus)
    {
        var map = Server.MapName;
        var where = bonus == 0 ? map : $"{map} bonus {bonus}";
        var best = _records.Top(map, RecordStore.Track(bonus), 1).FirstOrDefault();

        Chat.To(player, best.Record == null
            ? $"No one has finished {where} yet."
            : $"Record on {where}: {ChatColors.Gold}{RecordStore.Format(best.Record.Time)}{ChatColors.Default} by {ChatColors.Green}{best.Record.Name}");
    }

    private void ShowPersonalBest(CCSPlayerController player)
    {
        var map = Server.MapName;
        var mine = _records.Best(map, "main", player.SteamID);

        Chat.To(player, mine == null
            ? $"You have not finished {map} yet."
            : $"Your best on {map}: {ChatColors.Gold}{RecordStore.Format(mine.Time)}{ChatColors.Default} · rank {_records.Rank(map, "main", player.SteamID)}/{_records.Count(map, "main")}");
    }

    private void ShowTop(CCSPlayerController player)
    {
        var map = Server.MapName;
        var top = _records.Top(map, "main", 10);

        if (top.Count == 0)
        {
            Chat.To(player, $"No one has finished {map} yet.");
            return;
        }

        Chat.To(player, $"Top times on {ChatColors.Gold}{map}");

        for (var i = 0; i < top.Count; i++)
        {
            Chat.To(player, $"{i + 1}. {ChatColors.Gold}{RecordStore.Format(top[i].Record.Time)}{ChatColors.Default} {top[i].Record.Name}");
        }
    }

    private void ShowInfo(CCSPlayerController player)
    {
        var map = Server.MapName;
        var bonuses = Zones().Where(entry => entry.Zone is { Kind: ZoneKind.Start } && entry.Zone.Bonus > 0)
            .Select(entry => entry.Zone.Bonus).Distinct().Count();

        Chat.To(player, HasZones()
            ? $"{ChatColors.Gold}{map}{ChatColors.Default} · {(bonuses > 0 ? $"{bonuses} bonus(es), !b N" : "no bonuses")}"
            : $"{ChatColors.Gold}{map}{ChatColors.Default} has no timer zones, so times are not recorded here.");
        ShowRecord(player, 0);
        ShowPersonalBest(player);
    }

    private static void SetMoveType(CCSPlayerPawn pawn, MoveType_t moveType)
    {
        pawn.MoveType = moveType;
        Schema.SetSchemaValue(pawn.Handle, "CBaseEntity", "m_nActualMoveType", (byte)moveType);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    private void EnterPractice(CCSPlayerController player, bool noclip)
    {
        if (AlivePawn(player) is not { } pawn)
        {
            return;
        }

        var run = RunOf(player.Slot);

        if (!run.Practice)
        {
            var origin = pawn.AbsOrigin!;
            var angles = pawn.EyeAngles;
            var velocity = pawn.AbsVelocity;
            run.Saved = new Saved(
                new Vector(origin.X, origin.Y, origin.Z),
                new QAngle(angles.X, angles.Y, angles.Z),
                new Vector(velocity.X, velocity.Y, velocity.Z),
                run.Track,
                run.Started is { } started ? Server.CurrentTime - started : null
            );
            run.Practice = true;
            run.Started = null;
            Chat.To(player, $"Practice mode: the timer is paused. {ChatColors.Green}!resume{ChatColors.Default} to carry on, {ChatColors.Green}!r{ChatColors.Default} to start over.");
        }

        if (noclip)
        {
            SetMoveType(pawn, MoveType_t.MOVETYPE_NOCLIP);
        }
    }

    private void ToggleNoclip(CCSPlayerController player)
    {
        if (AlivePawn(player) is not { } pawn)
        {
            return;
        }

        if (pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP)
        {
            SetMoveType(pawn, MoveType_t.MOVETYPE_WALK);
            return;
        }

        EnterPractice(player, noclip: true);
    }

    private static void LeavePractice(CCSPlayerPawn pawn, Run run)
    {
        if (pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP)
        {
            SetMoveType(pawn, MoveType_t.MOVETYPE_WALK);
        }

        run.Practice = false;
        run.Saved = null;
    }

    private void Resume(CCSPlayerController player)
    {
        var run = RunOf(player.Slot);

        if (!run.Practice || run.Saved is not { } saved || AlivePawn(player) is not { } pawn)
        {
            Chat.To(player, "You are not in practice mode.");
            return;
        }

        LeavePractice(pawn, run);
        pawn.Teleport(saved.Origin, saved.Angles, saved.Velocity);
        run.Track = saved.Track;
        run.Started = saved.Elapsed is { } elapsed ? Server.CurrentTime - (float)elapsed : null;
    }
}
