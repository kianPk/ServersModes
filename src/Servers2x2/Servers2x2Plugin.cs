using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using ServersModes.Shared;

namespace ServersModes.Wingman;

public sealed class Servers2x2Plugin : BasePlugin
{
    public override string ModuleName => "Servers 2x2";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "kian";
    public override string ModuleDescription => "The 2x2 (Wingman) map rotation and its vote.";

    private static readonly ServerMap[] Maps =
    [
        new("Inferno", "de_inferno"),
        new("Nuke", "de_nuke"),
        new("Overpass", "de_overpass"),
        new("Vertigo", "de_vertigo"),
        new("Poseidon", "3522144043"),
    ];

    public override void Load(bool hotReload)
    {
        Chat.Tag = "2x2";

        var words = new ChatWords(this);
        _ = new MapVote(this, words, Maps, 5, MapEnd.Match);

        RegisterEventHandler<EventPlayerConnectFull>(OnConnect);
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
            Chat.To(player!, $"Welcome to {ChatColors.Gold}2x2{ChatColors.Default}: Wingman rules, first to 9 rounds wins.");
            Chat.To(player!, $"The next map is voted at match point. {ChatColors.Green}!rtv !nominate !timeleft !nextmap");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }
}
