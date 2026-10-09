using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;

namespace ServersModes.Shared;

// The map pool an operator keeps on the site. The api serves it publicly at
// /hosted-servers/section-maps/<mode>; API_DOMAIN is set on every
// dedicated server's container.
public sealed partial class MapPool
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger _logger;
    private readonly string? _url;

    public MapPool(ILogger logger, string mode)
    {
        _logger = logger;

        var api = Environment.GetEnvironmentVariable("API_DOMAIN")?.Trim().TrimEnd('/');

        if (!string.IsNullOrEmpty(api))
        {
            _url = $"{(api.StartsWith("http") ? api : $"https://{api}")}/hosted-servers/section-maps/{mode}";
        }
    }

    // Calls back on the game thread, and only with a usable pool: an api that
    // is down leaves the rotation as it was.
    public void Refresh(Action<List<ServerMap>> apply)
    {
        if (_url == null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var response = JsonSerializer.Deserialize<Response>(await Http.GetStringAsync(_url), Json);
                var maps = (response?.Maps ?? [])
                    .Where(map => map.Id != null && MapId().IsMatch(map.Id) && !string.IsNullOrWhiteSpace(map.Name))
                    .Select(map => new ServerMap(map.Name!.Trim(), map.Id!))
                    .DistinctBy(map => map.Id)
                    .ToList();

                if (maps.Count > 0)
                {
                    Server.NextFrame(() => apply(maps));
                }
            }
            catch (Exception error)
            {
                _logger.LogWarning("Could not fetch the map pool from {Url}: {Error}", _url, error.Message);
            }
        });
    }

    // What the api lets through, and all that may reach the console.
    [GeneratedRegex(@"^(\d{6,12}|[a-z][a-z0-9_]{1,63})$")]
    public static partial Regex MapId();

    private sealed record Response(List<Entry>? Maps);

    private sealed record Entry(string? Id, string? Name);
}
