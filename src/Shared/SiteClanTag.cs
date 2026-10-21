using System.Collections.Concurrent;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace ServersModes.Shared;

// Club members get their club tag on TAB. Everyone else keeps the site host
// so a Servers section still advertises where to go.
public static class SiteClanTag
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<string, string> ClubTags = new();
    private static string? _url;
    private static ILogger? _logger;

    public static void Register(BasePlugin plugin, ILogger? logger = null)
    {
        _logger = logger;
        _url = ClanTagsUrl();

        plugin.RegisterEventHandler<EventPlayerConnectFull>((@event, _) =>
        {
            if (Players.IsHuman(@event.Userid))
            {
                Apply(@event.Userid!);
            }

            return HookResult.Continue;
        });

        plugin.RegisterEventHandler<EventPlayerSpawn>((@event, _) =>
        {
            if (Players.IsHuman(@event.Userid))
            {
                Apply(@event.Userid!);
            }

            return HookResult.Continue;
        });

        plugin.AddTimer(5f, Enforce, TimerFlags.REPEAT);
        plugin.AddTimer(30f, RefreshClubTags, TimerFlags.REPEAT);
        RefreshClubTags();
    }

    public static void Apply(CCSPlayerController player)
    {
        if (!Players.IsHuman(player))
        {
            return;
        }

        var tag = TagFor(player);
        if (tag is null || player.Clan == tag)
        {
            return;
        }

        player.Clan = tag;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
    }

    private static string? TagFor(CCSPlayerController player)
    {
        if (ClubTags.TryGetValue(player.SteamID.ToString(), out var club) && !string.IsNullOrWhiteSpace(club))
        {
            return $"[{club.Trim()}]";
        }

        return SiteHost.ClanTag();
    }

    private static void Enforce()
    {
        foreach (var player in Players.Humans())
        {
            Apply(player);
        }
    }

    private static void RefreshClubTags()
    {
        if (_url is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var response = JsonSerializer.Deserialize<Response>(await Http.GetStringAsync(_url), Json);
                var next = response?.Tags ?? new Dictionary<string, string>();
                foreach (var key in ClubTags.Keys)
                {
                    if (!next.ContainsKey(key))
                    {
                        ClubTags.TryRemove(key, out _);
                    }
                }

                foreach (var (steamId, tag) in next)
                {
                    if (!string.IsNullOrWhiteSpace(steamId) && !string.IsNullOrWhiteSpace(tag))
                    {
                        ClubTags[steamId] = tag.Trim();
                    }
                }
            }
            catch (Exception error)
            {
                _logger?.LogWarning("Could not fetch club clan tags from {Url}: {Error}", _url, error.Message);
            }
        });
    }

    private static string? ClanTagsUrl()
    {
        var api = Environment.GetEnvironmentVariable("API_DOMAIN")?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(api))
        {
            return null;
        }

        return $"{(api.StartsWith("http") ? api : $"https://{api}")}/clubs/clan-tags";
    }

    private sealed record Response(Dictionary<string, string>? Tags);
}
