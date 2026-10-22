using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace ServersModes.Shared;

// Club challenges on this mode's servers: count kills / noscopes / wins,
// flash progress on death and every few minutes so a player stays to finish.
public static class ClubChallenges
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string _mode = "";
    private static string? _api;
    private static ILogger? _logger;
    private static readonly Dictionary<ulong, List<Row>> Last = new();

    public static void Register(BasePlugin plugin, ILogger? logger, string mode)
    {
        _logger = logger;
        _mode = mode;
        _api = ApiRoot();
        if (_api is null)
        {
            return;
        }

        plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
        plugin.RegisterEventHandler<EventPlayerHurt>(OnHurt);
        plugin.AddTimer(180f, PingEveryone, TimerFlags.REPEAT);
    }

    public static void RecordMetric(CCSPlayerController? player, string metric, int amount = 1)
    {
        if (!Players.IsHuman(player) || string.IsNullOrEmpty(_api))
        {
            return;
        }

        _ = Send(player!, weapon: null, noscope: false, headshot: false, metric, amount, show: true, seconds: 5);
    }

    private static HookResult OnDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;

        if (Players.IsHuman(attacker) && attacker != victim && !SameSide(attacker, victim))
        {
            _ = Send(attacker!, @event.Weapon, @event.Noscope, @event.Headshot, null, 1, show: true, seconds: 5);
        }

        if (Players.IsHuman(@event.Assister) && @event.Assister != victim && !SameSide(@event.Assister, victim))
        {
            _ = Send(@event.Assister!, weapon: null, noscope: false, headshot: false, "assists", 1, show: false, seconds: 0);
        }

        if (Players.IsHuman(victim) && victim != attacker)
        {
            _ = Show(victim!, 5);
        }

        return HookResult.Continue;
    }

    private static HookResult OnHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        var damage = @event.DmgHealth;
        if (damage <= 0 || !Players.IsHuman(attacker) || attacker == victim || SameSide(attacker, victim))
        {
            return HookResult.Continue;
        }

        _ = Send(attacker!, @event.Weapon, noscope: false, headshot: false, "damage", damage, show: false, seconds: 0);
        return HookResult.Continue;
    }

    private static bool SameSide(CCSPlayerController? a, CCSPlayerController? b)
    {
        if (_mode is "dm" or "awp" or "duels" or "bhop")
        {
            return false;
        }

        return a is { TeamNum: 2 or 3 } && b is not null && a.TeamNum == b.TeamNum;
    }

    private static void PingEveryone()
    {
        foreach (var player in Players.Humans())
        {
            _ = Show(player, 4);
        }
    }

    private static async Task Send(
        CCSPlayerController player,
        string? weapon,
        bool noscope,
        bool headshot,
        string? metric,
        int amount,
        bool show,
        int seconds)
    {
        var steamId = player.SteamID.ToString();
        try
        {
            var response = await Http.PostAsJsonAsync(
                $"{_api}/clubs/challenges/events",
                new EventBody(_mode, steamId, weapon, noscope, headshot, metric, amount, ServerId()),
                Json);
            var payload = await response.Content.ReadFromJsonAsync<EventReply>(Json);
            var rows = payload?.Progress ?? [];
            Last[player.SteamID] = rows;
            if (show && rows.Count > 0)
            {
                var snapshot = player;
                Server.NextFrame(() => Paint(snapshot, rows, seconds));
            }
        }
        catch (Exception error)
        {
            _logger?.LogWarning("Club challenge event failed: {Error}", error.Message);
        }
    }

    private static async Task Show(CCSPlayerController player, int seconds)
    {
        var steamId = player.SteamID.ToString();
        try
        {
            var server = ServerId();
            var live =
                $"{_api}/clubs/challenges/live?mode={Uri.EscapeDataString(_mode)}&steam_id={steamId}";
            if (!string.IsNullOrEmpty(server))
            {
                live += $"&server_id={Uri.EscapeDataString(server)}";
            }

            var payload = await Http.GetFromJsonAsync<LiveReply>(live, Json);
            var rows = (payload?.Challenges ?? [])
                .Where(row => row.Target > 0 && row.Progress < row.Target)
                .Select(row => new Row(
                    row.Id ?? "",
                    row.Title ?? "",
                    row.TitleFa ?? row.Title ?? "",
                    row.Progress,
                    row.Target,
                    false,
                    false))
                .Take(3)
                .ToList();
            if (rows.Count == 0 && Last.TryGetValue(player.SteamID, out var cached))
            {
                rows = cached.Where(row => !row.Completed && row.Current < row.Target).Take(3).ToList();
            }

            if (rows.Count == 0)
            {
                return;
            }

            var snapshot = player;
            Server.NextFrame(() => Paint(snapshot, rows, seconds));
        }
        catch (Exception error)
        {
            _logger?.LogWarning("Club challenge live failed: {Error}", error.Message);
        }
    }

    private static void Paint(CCSPlayerController player, List<Row> rows, int seconds)
    {
        if (!Players.IsHuman(player))
        {
            return;
        }

        var open = rows.Where(row => !row.Completed).Take(2).ToList();
        var done = rows.FirstOrDefault(row => row.JustCompleted || row.Completed);
        if (open.Count == 0 && done is null)
        {
            return;
        }

        var line = done is { } finished
            ? $"<font color='#46a758'><b>چلنج تمام شد</b></font><br>{Html(finished.TitleFa)} · جایزه واریز شد"
            : string.Join("<br>", open.Select(row =>
                $"<font color='#f5a524'><b>{Html(row.TitleFa)}</b></font>  {row.Current}/{row.Target} — نرو، تمومش کن"));

        player.PrintToCenterHtml(line, seconds);
        foreach (var row in open)
        {
            Chat.To(player, $"{ChatColors.Gold}{row.TitleFa}{ChatColors.Default} {ChatColors.Green}{row.Current}{ChatColors.Default}/{row.Target}");
        }
    }

    private static string Html(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string? ServerId()
    {
        var id = Environment.GetEnvironmentVariable("SERVER_ID")?.Trim();
        return string.IsNullOrEmpty(id) ? null : id;
    }

    private static string? ApiRoot()
    {
        var api = Environment.GetEnvironmentVariable("API_DOMAIN")?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(api))
        {
            return null;
        }

        return api.StartsWith("http") ? api : $"https://{api}";
    }

    private sealed record EventBody(
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("steam_id")] string SteamId,
        [property: JsonPropertyName("weapon")] string? Weapon,
        [property: JsonPropertyName("noscope")] bool Noscope,
        [property: JsonPropertyName("headshot")] bool Headshot,
        [property: JsonPropertyName("metric")] string? Metric,
        [property: JsonPropertyName("amount")] int Amount,
        [property: JsonPropertyName("server_id")] string? ServerId);

    private sealed record EventReply(
        [property: JsonPropertyName("progress")] List<Row>? Progress);

    private sealed record LiveReply(
        [property: JsonPropertyName("challenges")] List<LiveRow>? Challenges);

    private sealed record LiveRow(
        string? Id,
        string? Title,
        [property: JsonPropertyName("title_fa")] string? TitleFa,
        int Progress,
        int Target);

    private sealed record Row(
        string Id,
        string Title,
        [property: JsonPropertyName("title_fa")] string TitleFa,
        [property: JsonPropertyName("current")] int Current,
        int Target,
        bool Completed,
        [property: JsonPropertyName("just_completed")] bool JustCompleted);
}
