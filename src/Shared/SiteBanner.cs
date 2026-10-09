using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace ServersModes.Shared;

// The site's address, in the panel at the bottom of the screen for a
// player's first seconds on the server (once, not again on every map).
// WEB_DOMAIN is set on every dedicated server's container; one created before
// it only has API_DOMAIN, whose "api." host leads to the same site.
public sealed class SiteBanner
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Length = TimeSpan.FromSeconds(15);

    private readonly string? _site = Site();
    private readonly HashSet<ulong> _seen = new();
    private readonly Dictionary<int, (DateTime From, DateTime Until)> _showing = new();

    public SiteBanner(BasePlugin plugin)
    {
        if (_site == null)
        {
            return;
        }

        plugin.RegisterEventHandler<EventPlayerConnectFull>(OnConnect);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        plugin.AddTimer(1f, Show, TimerFlags.REPEAT);
    }

    // For a plugin with a panel of its own to stand aside.
    public bool Showing(int slot) =>
        _showing.TryGetValue(slot, out var window) && DateTime.UtcNow >= window.From && DateTime.UtcNow < window.Until;

    private HookResult OnConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (Players.IsHuman(player) && _seen.Add(player!.SteamID))
        {
            var from = DateTime.UtcNow + Delay;
            _showing[player.Slot] = (from, from + Length);
        }

        return HookResult.Continue;
    }

    private HookResult OnDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
        {
            _showing.Remove(player.Slot);
        }

        return HookResult.Continue;
    }

    private void Show()
    {
        var now = DateTime.UtcNow;

        foreach (var (slot, window) in _showing.ToList())
        {
            if (now >= window.Until)
            {
                _showing.Remove(slot);
                continue;
            }

            if (now < window.From || Players.Humans().FirstOrDefault(player => player.Slot == slot) is not { } player)
            {
                continue;
            }

            if (now - window.From < TimeSpan.FromSeconds(1))
            {
                Chat.To(player, $"Stats, ranks and skins: {ChatColors.Gold}{_site}");
            }

            player.PrintToCenterHtml(
                $"<font color='#9aa0a6'>Stats, ranks and skins at</font><br><font class='fontSize-xl' color='#f5a524'>{_site}</font>",
                2
            );
        }
    }

    private static string? Site()
    {
        if (Host(Environment.GetEnvironmentVariable("WEB_DOMAIN")) is { } web)
        {
            return web;
        }

        return Host(Environment.GetEnvironmentVariable("API_DOMAIN")) is { } api && api.StartsWith("api.")
            ? api["api.".Length..]
            : null;
    }

    private static string? Host(string? value)
    {
        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return Uri.TryCreate(value.Contains("://") ? value : $"https://{value}", UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }
}
