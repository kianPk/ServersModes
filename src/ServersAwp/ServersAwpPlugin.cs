using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using ServersModes.Shared;

namespace ServersModes.Awp;

public sealed class ServersAwpPlugin : BasePlugin
{
    public override string ModuleName => "Servers AWP";
    public override string ModuleVersion => "1.3.0";
    public override string ModuleAuthor => "kian";
    public override string ModuleDescription => "AWP-only rounds, team against team, on a map only an admin changes.";

    private static readonly ServerMap[] Maps =
    [
        new("awp_lego_2", "3077655898"),
        new("awp_creek", "3081154235"),
        new("awp_roost_fp", "3070577601"),
        new("awp_gony_v2", "3094723224"),
    ];

    public override void Load(bool hotReload)
    {
        Chat.Tag = "AWP";

        var words = new ChatWords(this);
        _ = new MapVote(this, words, "awp", Maps, 4, MapEnd.Manual);
        _ = new SiteBanner(this);
        NoHealthshot.Register(this);
        NoBots.Register(this);

        words.Add(this, "rs", "Reset your score", (player, _) => ResetScore(player));
        words.Add(this, "spec", "Watch instead of playing", (player, _) => player.ChangeTeam(CsTeam.Spectator));

        RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        RegisterEventHandler<EventPlayerConnectFull>(OnConnect);
    }

    // The cfg already hands out an AWP; this makes the loadout exact, whatever
    // the map places or gives (a C4 on a map with bomb sites).
    private HookResult OnSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is not { IsValid: true } || player.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
        {
            return HookResult.Continue;
        }

        AddTimer(0.1f, () =>
        {
            if (!player.IsValid || player.PlayerPawn.Value is not { LifeState: (byte)LifeState_t.LIFE_ALIVE })
            {
                return;
            }

            player.RemoveWeapons();
            player.GiveNamedItem("weapon_knife");
            player.GiveNamedItem("weapon_awp");
            player.GiveNamedItem("item_assaultsuit");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private static void ResetScore(CCSPlayerController player)
    {
        if (player.ActionTrackingServices?.MatchStats is { } stats)
        {
            stats.Kills = 0;
            stats.Deaths = 0;
            stats.Assists = 0;
            stats.Damage = 0;
            stats.HeadShotKills = 0;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pActionTrackingServices");
        }

        player.Score = 0;
        player.MVPs = 0;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_iScore");
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMVPs");

        Chat.To(player, "Your score is back to zero.");
    }

    private HookResult OnConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (!Players.IsHuman(player))
        {
            return HookResult.Continue;
        }

        AddTimer(5f, () =>
        {
            Chat.To(player!, $"Welcome to {ChatColors.Gold}AWP{ChatColors.Default}: team against team, AWP and knife only, free armour, unlimited ammo.");
            Chat.To(player!, $"{ChatColors.Green}!rs{ChatColors.Default} resets your score, {ChatColors.Green}!spec{ChatColors.Default} lets you watch.");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }
}
