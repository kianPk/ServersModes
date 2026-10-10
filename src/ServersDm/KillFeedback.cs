using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ServersModes.Shared;

namespace ServersModes.Dm;

// What a player hears and sees for their own hits and kills: a short ding on
// every headshot and a light tick on every body shot that does not kill, a
// kill sound (a heavier one for a headshot),
// and the announcer for kills in quick succession and for kill streaks.
// The announcer's voice lines come from a workshop addon that
// MultiAddonManager makes every client download; without it they are silent
// and only the callout text shows.
public sealed class KillFeedback
{
    public const string AnnouncerAddon = "3461824328";
    private const string AnnouncerSoundEvents = "soundevents/soundevents_quakesounds.vsndevts";

    private const float MultiKillWindow = 4f;

    private const string HeadshotHitSound = "sounds/buttons/bell1.vsnd_c";
    private const string BodyHitSound = "sounds/buttons/blip1.vsnd_c";
    private const string KillSound = "sounds/ui/armsrace_kill_01.vsnd_c";
    private const string HeadshotKillSound = "sounds/buttons/bell1.vsnd_c";

    private static readonly (string Name, string Color, string Sound)[] MultiKills =
    [
        ("DOUBLE KILL", "#46a758", "QuakeSoundsD.Doublekill"),
        ("TRIPLE KILL", "#3e9bf5", "QuakeSoundsD.Triplekill"),
        ("ULTRA KILL", "#a855f7", "QuakeSoundsD.Ultrakill"),
        ("MONSTER KILL", "#f5a524", "QuakeSoundsD.Monsterkill"),
        ("LUDICROUS KILL", "#e5484d", "QuakeSoundsD.Ludicrouskill"),
    ];

    private static readonly Dictionary<int, (string Name, string Sound)> Streaks = new()
    {
        [5] = ("KILLING SPREE", "QuakeSoundsD.Killingspree"),
        [10] = ("RAMPAGE", "QuakeSoundsD.Rampage"),
        [15] = ("DOMINATING", "QuakeSoundsD.Dominating"),
        [20] = ("UNSTOPPABLE", "QuakeSoundsD.Unstoppable"),
        [25] = ("GODLIKE", "QuakeSoundsD.Godlike"),
    };

    private const int HitgroupHead = 1;

    private sealed class Tally
    {
        public int Chain;
        public float Last;
        public int Streak;
    }

    private readonly Func<CCSPlayerController, bool> _wantsSounds;
    private readonly ILogger _logger;
    private readonly Dictionary<int, Tally> _tallies = new();

    // MultiAddonManager mounts what mm_extra_addons lists on the next map
    // load, so an addon added here is heard from the next map on.
    private void CheckAnnouncerAddon()
    {
        var addons = ConVar.Find("mm_extra_addons");

        if (addons == null)
        {
            _logger.LogWarning("MultiAddonManager is not loaded: the kill announcer (workshop addon {Addon}) stays silent", AnnouncerAddon);
            return;
        }

        var listed = addons.StringValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (listed.Contains(AnnouncerAddon))
        {
            _logger.LogInformation("MultiAddonManager mounts {Addons}", addons.StringValue);
            return;
        }

        var value = string.Join(',', listed.Append(AnnouncerAddon));
        Server.ExecuteCommand($"mm_extra_addons \"{value}\"");
        _logger.LogWarning("mm_extra_addons lacked the announcer addon; set it to {Addons}, mounted from the next map", value);
    }

    // MultiAddonManager tries its download once, when the Steam API comes up,
    // which is before the server has logged on, so the download fails and the
    // server runs without the addon until the map changes. Setting the addons
    // again makes it retry; it reloads the map itself once the download is done.
    private const int MaxDownloadRetries = 5;
    private int _downloadRetries;

    private void RetryAnnouncerDownload()
    {
        if (_downloadRetries >= MaxDownloadRetries || ConVar.Find("mm_extra_addons") is not { } addons || AnnouncerInstalled())
        {
            return;
        }

        _downloadRetries++;
        var value = addons.StringValue;
        Server.ExecuteCommand($"mm_extra_addons \"\";mm_extra_addons \"{value}\"");
        _logger.LogWarning("Announcer addon {Addon} is not on the server yet; asked MultiAddonManager to download it again ({Try}/{Max})",
            AnnouncerAddon, _downloadRetries, MaxDownloadRetries);
    }

    // Where MultiAddonManager looks for it: game/bin/linuxsteamrt64/steamapps.
    private static bool AnnouncerInstalled()
    {
        var game = Server.GameDirectory;

        return new[] { game, Path.GetDirectoryName(game.TrimEnd('/')) ?? game }
            .Select(root => Path.Combine(root, "bin", "linuxsteamrt64", "steamapps", "workshop", "content", "730", AnnouncerAddon))
            .Any(Directory.Exists);
    }

    public KillFeedback(BasePlugin plugin, Func<CCSPlayerController, bool> wantsSounds)
    {
        _wantsSounds = wantsSounds;
        _logger = plugin.Logger;
        CheckAnnouncerAddon();
        plugin.RegisterListener<Listeners.OnMapStart>(_ => CheckAnnouncerAddon());
        plugin.AddTimer(30f, RetryAnnouncerDownload, TimerFlags.REPEAT);
        plugin.RegisterListener<Listeners.OnServerPrecacheResources>(manifest => manifest.AddResource(AnnouncerSoundEvents));
        plugin.RegisterEventHandler<EventPlayerHurt>(OnHurt);
        plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
        plugin.RegisterEventHandler<EventPlayerDisconnect>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } player)
            {
                _tallies.Remove(player.Slot);
            }

            return HookResult.Continue;
        });
        plugin.RegisterListener<Listeners.OnMapStart>(_ => _tallies.Clear());
    }

    private HookResult OnHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;

        if (@event.Health > 0 && Players.IsHuman(attacker) && attacker != @event.Userid)
        {
            PlayFile(attacker!, @event.Hitgroup == HitgroupHead ? HeadshotHitSound : BodyHitSound);
        }

        return HookResult.Continue;
    }

    private HookResult OnDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } victim)
        {
            _tallies.Remove(victim.Slot);
        }

        var attacker = @event.Attacker;

        if (!Players.IsHuman(attacker) || attacker == @event.Userid)
        {
            return HookResult.Continue;
        }

        var now = Server.CurrentTime;

        if (!_tallies.TryGetValue(attacker!.Slot, out var tally))
        {
            tally = new Tally();
            _tallies[attacker.Slot] = tally;
        }

        tally.Chain = now - tally.Last <= MultiKillWindow ? tally.Chain + 1 : 1;
        tally.Last = now;
        tally.Streak++;

        if (tally.Chain >= 2)
        {
            var (name, color, sound) = MultiKills[Math.Min(tally.Chain - 2, MultiKills.Length - 1)];
            PlayEvent(attacker, sound);
            Callout(attacker, name, color);

            if (tally.Chain >= 4)
            {
                Chat.All($"{ChatColors.Green}{attacker.PlayerName}{ChatColors.Default} got an {ChatColors.Gold}{name}{ChatColors.Default}!");
            }
        }
        else if (Streaks.TryGetValue(tally.Streak, out var streak))
        {
            PlayEvent(attacker, streak.Sound);
            Callout(attacker, streak.Name, "#f5a524");
        }
        else
        {
            PlayFile(attacker, @event.Headshot ? HeadshotKillSound : KillSound);
        }

        if (Streaks.TryGetValue(tally.Streak, out var reached))
        {
            Chat.All($"{ChatColors.Green}{attacker.PlayerName}{ChatColors.Default} is on a {ChatColors.Gold}{reached.Name}{ChatColors.Default} ({tally.Streak} kills)!");
        }

        return HookResult.Continue;
    }

    private static void Callout(CCSPlayerController player, string name, string color) =>
        player.PrintToCenterHtml($"<font class='fontSize-xl' color='{color}'><b>{name}</b></font>", 2);

    private void PlayFile(CCSPlayerController player, string sound)
    {
        if (_wantsSounds(player))
        {
            player.ExecuteClientCommand($"play {sound}");
        }
    }

    private void PlayEvent(CCSPlayerController player, string soundEvent)
    {
        if (_wantsSounds(player))
        {
            player.EmitSound(soundEvent, [player]);
        }
    }
}
