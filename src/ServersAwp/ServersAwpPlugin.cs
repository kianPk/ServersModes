using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using ServersModes.Shared;

namespace ServersModes.Awp;

public sealed class ServersAwpPlugin : BasePlugin
{
    public override string ModuleName => "Servers AWP";
    public override string ModuleVersion => "1.4.1";
    public override string ModuleAuthor => "kian";
    public override string ModuleDescription => "AWP-only deathmatch, everyone against everyone, on a map only an admin changes.";

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
        RegisterEventHandler<EventPlayerDeath>(OnDeath);
        RegisterEventHandler<EventItemPickup>(OnItemPickup);
        RegisterEventHandler<EventPlayerConnectFull>(OnConnect);
    }

    // The game respawns the dead (mp_respawn_on_death_*); a kill gives the
    // killer health and armour back so the next fight starts even.
    private HookResult OnDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;

        if (attacker is not { IsValid: true } || attacker == @event.Userid)
        {
            return HookResult.Continue;
        }

        Server.NextFrame(() =>
        {
            if (!attacker.IsValid || attacker.PlayerPawn.Value is not { LifeState: (byte)LifeState_t.LIFE_ALIVE } pawn)
            {
                return;
            }

            pawn.Health = 100;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
            pawn.ArmorValue = 100;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        });

        return HookResult.Continue;
    }

    // The cfg already hands out an AWP; this makes the loadout exact, whatever
    // the map places or gives (a C4 on a map with bomb sites), and clears the
    // deathmatch INVULNERABLE box that mp_respawn_immunitytime can still grant.
    private HookResult OnSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is not { IsValid: true } || player.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
        {
            return HookResult.Continue;
        }

        AddTimer(0.1f, () => EnforceLoadout(player), TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    // A pickup from the ground or a give from another plugin must not leave
    // the player holding anything but an AWP and a knife.
    private HookResult OnItemPickup(EventItemPickup @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (!Players.IsHuman(player))
        {
            return HookResult.Continue;
        }

        Server.NextFrame(() => EnforceLoadout(player!));

        return HookResult.Continue;
    }

    private static void EnforceLoadout(CCSPlayerController player)
    {
        if (!player.IsValid || player.PlayerPawn.Value is not { LifeState: (byte)LifeState_t.LIFE_ALIVE } pawn)
        {
            return;
        }

        ClearImmunity(pawn);

        if (pawn.WeaponServices is { } weapons)
        {
            var onlyAwpAndKnife = true;
            foreach (var handle in weapons.MyWeapons)
            {
                if (handle.Value is not { IsValid: true } weapon)
                {
                    continue;
                }

                var name = weapon.DesignerName;
                if (name is "weapon_awp" || name.Contains("knife", StringComparison.OrdinalIgnoreCase) || name is "weapon_bayonet")
                {
                    continue;
                }

                onlyAwpAndKnife = false;
                break;
            }

            if (onlyAwpAndKnife && HasDesigner(weapons, "weapon_awp"))
            {
                return;
            }
        }

        player.RemoveWeapons();
        player.GiveNamedItem("weapon_knife");
        player.GiveNamedItem("weapon_awp");
        player.GiveNamedItem("item_assaultsuit");
    }

    private static bool HasDesigner(CPlayer_WeaponServices weapons, string designer)
    {
        foreach (var handle in weapons.MyWeapons)
        {
            if (handle.Value is { IsValid: true } weapon &&
                weapon.DesignerName.Equals(designer, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Deathmatch's spawn immunity (the INVULNERABLE box), should a spawn have
    // granted it despite mp_respawn_immunitytime.
    private static void ClearImmunity(CCSPlayerPawn pawn)
    {
        if (pawn.GunGameImmunity)
        {
            pawn.GunGameImmunity = false;
            pawn.ImmuneToGunGameDamageTime = 0;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_bGunGameImmunity");
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_fImmuneToGunGameDamageTime");
        }
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
            Chat.To(player!, $"Welcome to {ChatColors.Gold}AWP{ChatColors.Default}: deathmatch, everyone against everyone, AWP and knife only. You respawn straight away and every kill gives your health back.");
            Chat.To(player!, $"{ChatColors.Green}!rs{ChatColors.Default} resets your score, {ChatColors.Green}!spec{ChatColors.Default} lets you watch.");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }
}
