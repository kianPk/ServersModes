using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace ServersModes.Shared;

// The one site level (from XP on the api). The TAB pin is this level on every
// mode, not a Faceit Elo band.
public static class SiteLevel
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Dictionary<ulong, int> Levels = new();
    private static readonly List<XpEvent> Pending = new();
    private static ILogger? _logger;
    private static string? _api;
    private static string? _serverId;
    private static string? _password;
    private static bool _ffa;

    public static void Register(BasePlugin plugin, ILogger? logger, string mode)
    {
        _logger = logger;
        _ffa = mode is "dm" or "awp" or "duels" or "bhop";
        _serverId = Environment.GetEnvironmentVariable("SERVER_ID");
        _password = Environment.GetEnvironmentVariable("SERVER_API_PASSWORD");
        var api = Environment.GetEnvironmentVariable("API_DOMAIN")?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(api))
        {
            _api = api.StartsWith("http") ? api : $"https://{api}";
        }

        plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
        plugin.RegisterEventHandler<EventPlayerHurt>(OnHurt);
        plugin.RegisterEventHandler<EventPlayerBlind>(OnBlind);
        plugin.RegisterEventHandler<EventBombPlanted>(OnPlant);
        plugin.RegisterEventHandler<EventBombDefused>(OnDefuse);
        plugin.RegisterEventHandler<EventPlayerConnectFull>(OnConnect);
        plugin.AddTimer(8f, () =>
        {
            Refresh(Players.Humans().Select(player => player.SteamID));
            Flush();
            Paint();
        }, TimerFlags.REPEAT);
    }

    public static int Level(ulong steamId) =>
        Levels.TryGetValue(steamId, out var level) ? level : 1;

    public static void Award(CCSPlayerController? player, string kind)
    {
        if (!Players.IsHuman(player))
        {
            return;
        }

        Queue(player!.SteamID, kind, null, false, false);
    }

    private static HookResult OnConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!Players.IsHuman(player))
        {
            return HookResult.Continue;
        }

        var steamId = player!.SteamID;
        Refresh([steamId], () =>
        {
            if (!Players.IsHuman(player))
            {
                return;
            }

            ScoreboardLevel.Apply(player, Level(steamId));
            Chat.To(player, $"لول سایت {ChatColors.Gold}{Level(steamId)}{ChatColors.Default}");
        });
        return HookResult.Continue;
    }

    private static HookResult OnDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (Players.IsHuman(attacker) && attacker != victim && !Teammates(attacker, victim))
        {
            Queue(attacker!.SteamID, "kill", @event.Weapon, @event.Headshot, @event.Noscope, victim?.SteamID);
            var assister = @event.Assister;
            if (Players.IsHuman(assister) && assister != attacker && !Teammates(assister, victim))
            {
                Queue(assister!.SteamID, "assist", null, false, false, victim?.SteamID);
            }
        }
        else if (Players.IsHuman(attacker) && attacker != victim && Teammates(attacker, victim))
        {
            Queue(attacker!.SteamID, "team_kill", @event.Weapon, false, false, victim?.SteamID);
        }

        if (Players.IsHuman(victim) && victim != attacker)
        {
            Queue(victim!.SteamID, "death", null, false, false);
        }

        return HookResult.Continue;
    }

    private static HookResult OnHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (@event.DmgHealth <= 0 || !Players.IsHuman(attacker) || attacker == victim)
        {
            return HookResult.Continue;
        }

        if (Teammates(attacker, victim))
        {
            Queue(attacker!.SteamID, "team_damage", @event.Weapon, false, false, victim?.SteamID);
        }
        else if (Utility(@event.Weapon))
        {
            Queue(attacker!.SteamID, "utility_damage", @event.Weapon, false, false, victim?.SteamID);
        }

        return HookResult.Continue;
    }

    private static HookResult OnBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (@event.BlindDuration < 0.5f || !Players.IsHuman(attacker) || attacker == victim)
        {
            return HookResult.Continue;
        }

        Queue(
            attacker!.SteamID,
            Teammates(attacker, victim) ? "flash_team" : "flash_enemy",
            null,
            false,
            false,
            victim?.SteamID);
        return HookResult.Continue;
    }

    private static HookResult OnPlant(EventBombPlanted @event, GameEventInfo info)
    {
        Award(@event.Userid, "plant");
        return HookResult.Continue;
    }

    private static HookResult OnDefuse(EventBombDefused @event, GameEventInfo info)
    {
        Award(@event.Userid, "defuse");
        return HookResult.Continue;
    }

    private static bool Teammates(CCSPlayerController? a, CCSPlayerController? b) =>
        !_ffa && a is { TeamNum: 2 or 3 } && b is not null && a.TeamNum == b.TeamNum;

    private static bool Utility(string? weapon)
    {
        var name = (weapon ?? "").Replace("weapon_", "");
        return name is "hegrenade" or "molotov" or "incgrenade" or "inferno" or "decoy";
    }

    private static void Queue(
        ulong steamId,
        string kind,
        string? weapon,
        bool headshot,
        bool noscope,
        ulong? victim = null)
    {
        if (steamId == 0 || string.IsNullOrEmpty(_api))
        {
            return;
        }

        lock (Pending)
        {
            Pending.Add(new XpEvent(
                steamId.ToString(),
                victim is > 0 ? victim.Value.ToString() : null,
                kind,
                weapon,
                headshot,
                noscope));
        }
    }

    private static void Paint()
    {
        var any = false;
        foreach (var player in Players.Humans())
        {
            ScoreboardLevel.Apply(player, Level(player.SteamID));
            any = true;
        }

        if (any)
        {
            ScoreboardLevel.RevealAll();
        }
    }

    private static void Refresh(IEnumerable<ulong> steamIds, Action? then = null)
    {
        var ids = steamIds.Where(id => id > 0).Distinct().Take(64).ToArray();
        if (ids.Length == 0 || string.IsNullOrEmpty(_api))
        {
            then?.Invoke();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var payload = await Http.GetFromJsonAsync<Envelope>(
                    $"{_api}/hosted-servers/site-xp?steam_ids={string.Join(',', ids)}",
                    Json);
                foreach (var player in payload?.Players ?? [])
                {
                    if (ulong.TryParse(player.SteamId, out var steamId))
                    {
                        Levels[steamId] = Math.Clamp(player.Level, 1, 10);
                    }
                }
            }
            catch (Exception error)
            {
                _logger?.LogWarning("Site level fetch failed: {Error}", error.Message);
            }

            if (then is not null)
            {
                Server.NextFrame(then);
            }
        });
    }

    private static void Flush()
    {
        if (string.IsNullOrEmpty(_api) || string.IsNullOrEmpty(_serverId) || string.IsNullOrEmpty(_password))
        {
            return;
        }

        List<XpEvent> batch;
        lock (Pending)
        {
            if (Pending.Count == 0)
            {
                return;
            }

            batch = Pending.Take(64).ToList();
            Pending.RemoveRange(0, batch.Count);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_api}/hosted-servers/plugin/site-xp");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _password);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(new { server_id = _serverId, events = batch }, Json),
                    Encoding.UTF8,
                    "application/json");
                using var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("Site XP post {Status}", (int)resp.StatusCode);
                    return;
                }

                var payload = JsonSerializer.Deserialize<Envelope>(await resp.Content.ReadAsStringAsync(), Json);
                foreach (var player in payload?.Players ?? [])
                {
                    if (ulong.TryParse(player.SteamId, out var steamId))
                    {
                        Levels[steamId] = Math.Clamp(player.Level, 1, 10);
                    }
                }
            }
            catch (Exception error)
            {
                _logger?.LogWarning("Site XP post failed: {Error}", error.Message);
            }
        });
    }

    private sealed class Envelope
    {
        [JsonPropertyName("players")]
        public List<PlayerDto> Players { get; set; } = [];
    }

    private sealed class PlayerDto
    {
        [JsonPropertyName("steam_id")]
        public string? SteamId { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; } = 1;
    }

    private sealed record XpEvent(
        [property: JsonPropertyName("steam_id")] string SteamId,
        [property: JsonPropertyName("victim_steam_id")] string? VictimSteamId,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("weapon")] string? Weapon,
        [property: JsonPropertyName("headshot")] bool Headshot,
        [property: JsonPropertyName("noscope")] bool Noscope);
}
