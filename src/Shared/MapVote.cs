using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace ServersModes.Shared;

// Id is a stock map name or a workshop id.
public sealed record ServerMap(string Name, string Id)
{
    public bool IsWorkshop => Id.All(char.IsDigit);
}

public enum MapEnd
{
    // mp_timelimit ends the map; the vote runs a few minutes before it does.
    Timed,

    // A match ends the map; the vote runs at match point.
    Match,
}

// The map rotation and its vote: an end-of-map vote, !rtv, !nominate,
// !timeleft and !nextmap. Maps change through changelevel/host_workshop_map,
// so the cfg leaves the end of the match to this (mp_match_end_restart 1,
// mp_match_end_changelevel 0, mp_endmatch_votenextmap 0).
//
// The pool starts as the plugin's own list and is replaced by the one an
// operator keeps on the site, fetched again on every map start. The site's
// "change map" sends css_servers_map <id> over rcon.
public sealed class MapVote
{
    private const float VoteSeconds = 20f;
    private const float TimedVoteLead = 180f;
    private const double RtvShare = 0.6;

    private readonly BasePlugin _plugin;
    private readonly MapPool _pool;
    private List<ServerMap> _maps;
    private readonly int _mapsToShow;
    private readonly MapEnd _end;
    private readonly Random _random = new();

    private ServerMap _current;
    private ServerMap? _previous;
    private ServerMap? _pending;
    private ServerMap? _next;
    private bool _voting;
    private bool _changeWhenDecided;
    private bool _changing;
    private readonly HashSet<ulong> _rtv = new();
    private readonly Dictionary<ulong, ServerMap> _nominations = new();
    private readonly Dictionary<ulong, ServerMap> _votes = new();
    private Timer? _voteTimer;
    private Timer? _changeTimer;

    public MapVote(BasePlugin plugin, ChatWords words, string mode, IReadOnlyList<ServerMap> maps, int mapsToShow, MapEnd end)
    {
        _plugin = plugin;
        _pool = new MapPool(plugin.Logger, mode);
        _maps = maps.ToList();
        _current = _maps[0];
        _mapsToShow = mapsToShow;
        _end = end;

        words.Add(plugin, "rtv", "Vote to change the map", (player, _) => RockTheVote(player));
        words.Add(plugin, "nominate", "Nominate a map for the vote", Nominate);
        words.Add(plugin, "timeleft", "Time left on this map", (player, _) => TimeLeft(player));
        words.Add(plugin, "nextmap", "Show the next map", (player, _) => Chat.To(player, NextMapText()));
        plugin.AddCommand("css_servers_map", "Change to a map now: <workshop id or map name>", OnChangeMapCommand);

        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        plugin.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
        plugin.AddTimer(5f, Tick, TimerFlags.REPEAT);

        _pool.Refresh(SetMaps);
    }

    private void SetMaps(List<ServerMap> maps)
    {
        _maps = maps;
        _current = Find(_current.Id) ?? _current;
    }

    private void OnMapStart(string mapName)
    {
        var arrived = _pending ?? Find(mapName);

        _previous = _current;
        _current = arrived ?? new ServerMap(mapName, mapName);
        _pending = null;

        // CS2 logs into Steam only once a map has loaded, and cannot download
        // a workshop map before it has, so a workshop mode boots on a stock map
        // and moves to its pool from here. A workshop map whose internal name
        // differs from ours is already on the pool, hence the stock-map test.
        if (arrived == null && IsStockMap(mapName))
        {
            _plugin.AddTimer(8f, () => Change(_maps[0]), TimerFlags.STOP_ON_MAPCHANGE);
        }

        _next = null;
        _voting = false;
        _changeWhenDecided = false;
        _changing = false;
        _rtv.Clear();
        _nominations.Clear();
        _votes.Clear();
        _voteTimer?.Kill();
        _voteTimer = null;
        _changeTimer = null;

        _pool.Refresh(SetMaps);
    }

    private void OnChangeMapCommand(CCSPlayerController? player, CommandInfo command)
    {
        var id = command.GetArg(1).Trim();

        // rcon only: a player typing it in their console has no say here.
        if (player != null || !MapPool.MapId().IsMatch(id))
        {
            return;
        }

        var map = Find(id) ?? new ServerMap(id, id);

        _voteTimer?.Kill();
        _voteTimer = null;
        _voting = false;
        _changeTimer?.Kill();
        _changing = false;
        _next = map;

        Chat.All($"An admin is changing the map to {ChatColors.Gold}{map.Name}{ChatColors.Default}.");
        ChangeAfter(3f);
    }

    private ServerMap? Find(string mapName)
    {
        var normalized = Normalize(mapName);
        return _maps.FirstOrDefault(map => Normalize(map.Id) == normalized || Normalize(map.Name) == normalized);
    }

    private static bool IsStockMap(string mapName) =>
        mapName.StartsWith("de_", StringComparison.OrdinalIgnoreCase)
        || mapName.StartsWith("cs_", StringComparison.OrdinalIgnoreCase)
        || mapName.StartsWith("ar_", StringComparison.OrdinalIgnoreCase);

    private void Change(ServerMap map)
    {
        _pending = map;
        Server.ExecuteCommand(map.IsWorkshop ? $"host_workshop_map {map.Id}" : $"changelevel {map.Id}");
    }

    private static string Normalize(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private HookResult OnDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is { IsValid: true })
        {
            _rtv.Remove(player.SteamID);
            _nominations.Remove(player.SteamID);
            _votes.Remove(player.SteamID);
            Server.NextFrame(CheckRtv);
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (_end != MapEnd.Match || _next != null || _voting || Players.IsWarmup())
        {
            return HookResult.Continue;
        }

        var maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 0;
        var toWin = maxRounds / 2 + 1;

        Server.NextFrame(() =>
        {
            var leader = Math.Max(Players.Score(CsTeam.Terrorist), Players.Score(CsTeam.CounterTerrorist));

            if (maxRounds > 0 && leader >= toWin - 1)
            {
                StartVote(changeWhenDecided: false);
            }
        });

        return HookResult.Continue;
    }

    private HookResult OnMatchEnd(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        if (_next != null)
        {
            ChangeAfter(8f);
        }
        else if (!_voting)
        {
            StartVote(changeWhenDecided: true);
        }
        else
        {
            _changeWhenDecided = true;
        }

        return HookResult.Continue;
    }

    private void Tick()
    {
        if (_end != MapEnd.Timed || _voting || _changing || Players.IsWarmup())
        {
            return;
        }

        var left = SecondsLeft();

        if (left == null)
        {
            return;
        }

        if (_next == null && left <= TimedVoteLead)
        {
            StartVote(changeWhenDecided: false);
        }
        // The win panel should have changed the map by now; a server that
        // never ends the match on time still has to move on.
        else if (_next != null && left <= -45)
        {
            ChangeAfter(0f);
        }
    }

    private static float? SecondsLeft()
    {
        var rules = Players.Rules();
        var limit = ConVar.Find("mp_timelimit")?.GetPrimitiveValue<float>() ?? 0f;

        if (rules == null || limit <= 0)
        {
            return null;
        }

        return rules.GameStartTime + limit * 60f - Server.CurrentTime;
    }

    private void RockTheVote(CCSPlayerController player)
    {
        if (_changing)
        {
            return;
        }

        if (_next != null)
        {
            Chat.To(player, $"The next map is already decided: {ChatColors.Gold}{_next.Name}");
            return;
        }

        if (_voting)
        {
            Chat.To(player, "A map vote is already running.");
            return;
        }

        if (!_rtv.Add(player.SteamID))
        {
            Chat.To(player, $"You already voted to change the map ({_rtv.Count}/{RtvNeeded()}).");
            return;
        }

        Chat.All($"{ChatColors.Gold}{player.PlayerName}{ChatColors.Default} wants to change the map ({_rtv.Count}/{RtvNeeded()}). Type {ChatColors.Green}!rtv{ChatColors.Default} to vote.");
        CheckRtv();
    }

    private static int RtvNeeded() => Math.Max(1, (int)Math.Ceiling(Players.Humans().Count() * RtvShare));

    private void CheckRtv()
    {
        if (_rtv.Count == 0 || _voting || _next != null || _changing)
        {
            return;
        }

        if (_rtv.Count >= RtvNeeded())
        {
            Chat.All("Enough players want a new map — the vote starts now.");
            StartVote(changeWhenDecided: true);
        }
    }

    private void Nominate(CCSPlayerController player, string[] args)
    {
        if (_next != null || _voting)
        {
            Chat.To(player, "The vote has already started — nominations are closed.");
            return;
        }

        var choices = _maps.Where(map => map != _current).ToList();

        if (args.Length > 0)
        {
            var query = Normalize(string.Join(' ', args));
            var match = choices.FirstOrDefault(map => Normalize(map.Name).Contains(query));

            if (match == null)
            {
                Chat.To(player, $"No map matches \"{string.Join(' ', args)}\". Type {ChatColors.Green}!nominate{ChatColors.Default} to see the list.");
                return;
            }

            NominateMap(player, match);
            return;
        }

        var menu = new ChatMenu("Nominate a map");

        foreach (var map in choices)
        {
            menu.AddMenuOption(map.Name, (chooser, _) => NominateMap(chooser, map));
        }

        MenuManager.OpenChatMenu(player, menu);
    }

    private void NominateMap(CCSPlayerController player, ServerMap map)
    {
        if (_next != null || _voting)
        {
            return;
        }

        _nominations[player.SteamID] = map;
        Chat.All($"{ChatColors.Gold}{player.PlayerName}{ChatColors.Default} nominated {ChatColors.Green}{map.Name}{ChatColors.Default}.");
    }

    private void TimeLeft(CCSPlayerController player)
    {
        if (_end == MapEnd.Timed)
        {
            var left = SecondsLeft();
            Chat.To(
                player,
                left == null
                    ? "This map has no time limit."
                    : $"Time left on {ChatColors.Green}{_current.Name}{ChatColors.Default}: {ChatColors.Gold}{Format(Math.Max(0, left.Value))}"
            );
            return;
        }

        var maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 0;
        Chat.To(
            player,
            $"First to {ChatColors.Gold}{maxRounds / 2 + 1}{ChatColors.Default} rounds — T {Players.Score(CsTeam.Terrorist)} : {Players.Score(CsTeam.CounterTerrorist)} CT."
        );
    }

    private static string Format(float seconds) => TimeSpan.FromSeconds(seconds).ToString(@"m\:ss");

    private string NextMapText() =>
        _next != null
            ? $"Next map: {ChatColors.Gold}{_next.Name}"
            : "The next map will be decided by vote.";

    private List<ServerMap> Options()
    {
        var options = new List<ServerMap>();

        foreach (var map in _nominations.Values.Distinct())
        {
            if (map != _current && options.Count < _mapsToShow)
            {
                options.Add(map);
            }
        }

        var previous = _previous != null && _previous != _current && _maps.Contains(_previous) ? _previous : null;
        var fresh = _maps.Where(map => map != _current && map != previous && !options.Contains(map)).OrderBy(_ => _random.Next());
        var cooled = previous != null && !options.Contains(previous) ? new[] { previous } : Array.Empty<ServerMap>();

        foreach (var map in fresh.Concat(cooled))
        {
            if (options.Count >= _mapsToShow)
            {
                break;
            }

            options.Add(map);
        }

        return options;
    }

    private void StartVote(bool changeWhenDecided)
    {
        var options = Options();
        _changeWhenDecided = changeWhenDecided;

        if (options.Count <= 1)
        {
            Decide(options.FirstOrDefault() ?? Rotation());
            return;
        }

        _voting = true;
        _votes.Clear();

        var menu = new ChatMenu("Vote for the next map");

        foreach (var map in options)
        {
            menu.AddMenuOption(map.Name, (voter, _) =>
            {
                if (!_voting)
                {
                    return;
                }

                _votes[voter.SteamID] = map;
                Chat.To(voter, $"You voted for {ChatColors.Green}{map.Name}{ChatColors.Default}.");
            });
        }

        Chat.All($"Vote for the next map! You have {ChatColors.Gold}{(int)VoteSeconds}{ChatColors.Default} seconds.");

        foreach (var player in Players.Humans())
        {
            MenuManager.OpenChatMenu(player, menu);
        }

        _voteTimer = _plugin.AddTimer(VoteSeconds, () =>
        {
            _voteTimer = null;
            _voting = false;

            var tally = _votes.Values.GroupBy(map => map).Select(group => (Map: group.Key, Votes: group.Count())).ToList();

            if (tally.Count == 0)
            {
                Decide(Rotation(), "Nobody voted, so the rotation continues.");
                return;
            }

            var most = tally.Max(entry => entry.Votes);
            var winners = tally.Where(entry => entry.Votes == most).ToList();
            var winner = winners[_random.Next(winners.Count)];

            Decide(winner.Map, $"{ChatColors.Green}{winner.Map.Name}{ChatColors.Default} won with {winner.Votes} vote(s).");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    // A map that has left the pool, or was never in it, carries on from the top.
    private ServerMap Rotation() => _maps[(_maps.IndexOf(_current) + 1) % _maps.Count];

    private void Decide(ServerMap map, string? reason = null)
    {
        _next = map;

        if (reason != null)
        {
            Chat.All(reason);
        }

        Chat.All(NextMapText());

        if (_changeWhenDecided)
        {
            ChangeAfter(5f);
        }
    }

    private void ChangeAfter(float seconds)
    {
        if (_changing || _next == null)
        {
            return;
        }

        _changing = true;
        var map = _next;
        Chat.All($"Changing the map to {ChatColors.Gold}{map.Name}{ChatColors.Default}…");

        _changeTimer = _plugin.AddTimer(seconds, () =>
        {
            _changeTimer = null;
            Change(map);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }
}
