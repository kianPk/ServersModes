using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace ServersModes.Shared;

// Elo for a Servers-section mode, kept on the api so a pod restart does not
// wipe Faceit-style levels. Auth is SERVER_ID + SERVER_API_PASSWORD.
public sealed class SectionRatings
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger _logger;
    private readonly string _mode;
    private readonly string? _baseUrl;
    private readonly string? _serverId = Environment.GetEnvironmentVariable("SERVER_ID");
    private readonly string? _password = Environment.GetEnvironmentVariable("SERVER_API_PASSWORD");
    private readonly Dictionary<ulong, double> _cache = new();
    private readonly HashSet<ulong> _dirty = new();
    private readonly object _gate = new();

    public const double Start = 1000;
    public const double Step = 32;

    public bool Configured =>
        !string.IsNullOrEmpty(_baseUrl) &&
        !string.IsNullOrEmpty(_serverId) &&
        !string.IsNullOrEmpty(_password);

    public SectionRatings(ILogger logger, string mode)
    {
        _logger = logger;
        _mode = mode;

        var api = Environment.GetEnvironmentVariable("API_DOMAIN")?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(api))
        {
            _baseUrl = $"{(api.StartsWith("http") ? api : $"https://{api}")}/hosted-servers/plugin/section-ratings/{mode}";
        }
    }

    public double Get(ulong steamId)
    {
        lock (_gate)
        {
            return _cache.TryGetValue(steamId, out var rating) ? rating : Start;
        }
    }

    public void Set(ulong steamId, double rating, bool persist = true)
    {
        lock (_gate)
        {
            _cache[steamId] = Math.Clamp(rating, 0, 5000);
            if (persist)
            {
                _dirty.Add(steamId);
            }
        }
    }

    // Elo change for a win. Returns the rounded delta applied to the winner.
    public int ApplyDuel(ulong winner, ulong loser)
    {
        var winnerRating = Get(winner);
        var loserRating = Get(loser);
        var expected = 1 / (1 + Math.Pow(10, (loserRating - winnerRating) / 400));
        var change = Step * (1 - expected);
        Set(winner, winnerRating + change);
        Set(loser, loserRating - change);
        return (int)Math.Round(change);
    }

    public void Fetch(IEnumerable<ulong> steamIds, Action? then = null)
    {
        if (!Configured)
        {
            then?.Invoke();
            return;
        }

        var ids = steamIds.Where(id => id > 0).Distinct().Take(64).ToArray();
        if (ids.Length == 0)
        {
            then?.Invoke();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var req = Request(HttpMethod.Get, $"?server_id={_serverId}&steam_ids={string.Join(',', ids)}");
                using var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("section ratings GET {Status}", (int)resp.StatusCode);
                    Server.NextFrame(() => then?.Invoke());
                    return;
                }

                var body = JsonSerializer.Deserialize<Envelope>(await resp.Content.ReadAsStringAsync(), Json);
                lock (_gate)
                {
                    foreach (var player in body?.Players ?? [])
                    {
                        if (ulong.TryParse(player.SteamId, out var steamId) && player.Rating is { } rating)
                        {
                            _cache[steamId] = rating;
                            _dirty.Remove(steamId);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                _logger.LogWarning("section ratings GET failed: {Error}", error.Message);
            }

            Server.NextFrame(() => then?.Invoke());
        });
    }

    public void Flush(Func<ulong, string?>? nameOf = null)
    {
        if (!Configured)
        {
            return;
        }

        List<(ulong SteamId, double Rating)> pending;
        lock (_gate)
        {
            pending = _dirty.Select(id => (id, _cache.GetValueOrDefault(id, Start))).ToList();
            if (pending.Count == 0)
            {
                return;
            }

            _dirty.Clear();
        }

        var payload = new
        {
            server_id = _serverId,
            players = pending.Select(row => new
            {
                steam_id = row.SteamId.ToString(),
                rating = row.Rating,
                name = nameOf?.Invoke(row.SteamId),
            }),
        };

        _ = Task.Run(async () =>
        {
            try
            {
                using var req = Request(HttpMethod.Post, "");
                req.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("section ratings POST {Status}", (int)resp.StatusCode);
                    lock (_gate)
                    {
                        foreach (var (steamId, _) in pending)
                        {
                            _dirty.Add(steamId);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                _logger.LogWarning("section ratings POST failed: {Error}", error.Message);
                lock (_gate)
                {
                    foreach (var (steamId, _) in pending)
                    {
                        _dirty.Add(steamId);
                    }
                }
            }
        });
    }

    private HttpRequestMessage Request(HttpMethod method, string query)
    {
        var req = new HttpRequestMessage(method, $"{_baseUrl}{query}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _password);
        return req;
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

        [JsonPropertyName("rating")]
        public double? Rating { get; set; }
    }
}
