using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using ServersModes.Shared;

namespace ServersModes.Awp;

public sealed class ServersAwpPlugin : BasePlugin
{
    public override string ModuleName => "Servers AWP";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "kian";
    public override string ModuleDescription => "AWP-only rules and the AWP map rotation.";

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
        _ = new MapVote(this, words, Maps, 4, MapEnd.Timed);

        RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        RegisterEventHandler<EventPlayerConnectFull>(OnConnect);
    }

    // The cfg already hands out an AWP; this makes the loadout exact, whatever
    // the map or a previous mode left lying around.
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

    private HookResult OnConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (!Players.IsHuman(player))
        {
            return HookResult.Continue;
        }

        AddTimer(5f, () =>
        {
            Chat.To(player!, $"Welcome to {ChatColors.Gold}AWP{ChatColors.Default}: AWP only, free armour, unlimited ammo.");
            Chat.To(player!, $"The map changes every 20 minutes. {ChatColors.Green}!rtv !nominate !timeleft !nextmap");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }
}
