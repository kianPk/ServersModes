using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ServersModes.Shared;

namespace ServersModes.Dm;

// Free-for-all deathmatch the way xplay runs it: the weapons each player picked
// with !guns on every spawn, health, armour and a full magazine back on every
// kill, a Medi-Shot for every few kills without dying, the game's line-of-sight
// spawns, and straight back in after a death instead of watching the killer.
// The map stays until an admin changes it from the site.
// The game respawns bots: a bot a plugin respawns comes back with its AI
// asleep and just stands there. Players are brought back by this plugin.
// Bots keep the server full, and each player who joins takes a bot's place
// (bot_quota_mode fill). They run the plugin's own behaviour tree (csgo/scripts/
// ai/servers_dm): the game's deathmatch one stalls them forever on a purchase
// when buying is off, so they get a rifle here instead, and its skill profiles
// are all the game's strongest.
public sealed class ServersDmPlugin : BasePlugin
{
    public override string ModuleName => "Servers DM";
    public override string ModuleVersion => "1.8.0";
    public override string ModuleAuthor => "kian";
    public override string ModuleDescription => "Free-for-all deathmatch with !guns, rewards on kill and the map rotation.";

    private static readonly ServerMap[] Maps =
    [
        new("Dust 2", "de_dust2"),
        new("Mirage", "de_mirage"),
        new("Anubis", "de_anubis"),
        new("Cache", "de_cache"),
        new("Ancient Night", "de_ancient_night"),
        new("Overpass", "de_overpass"),
        new("Ancient", "de_ancient"),
        new("Train", "de_train"),
        new("Inferno", "de_inferno"),
        new("Vertigo", "de_vertigo"),
        new("Nuke", "de_nuke"),
    ];

    // Long enough for the death to finish, too short to see the killer.
    private const float RespawnDelay = 0.1f;

    // A respawn that has not happened this long after it was due gets another try.
    private const float RespawnRetry = 2f;

    // Players plus bots; every player who joins replaces one bot.
    private const int BotQuota = 10;

    private const string BotTree = "scripts/ai/servers_dm/bt_default.kv3";

    // Without the plugin's tree on disk the game's stays: a missing tree would
    // leave bots with no AI at all.
    private static bool _hasBotTree;

    private static readonly string[] BotRifles = ["weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_ak47", "weapon_awp"];

    private LoadoutStore _loadouts = null!;
    private readonly Dictionary<int, float> _deadSince = new();

    // Buying is open everywhere and the wallet never runs dry.
    private const int Money = 16000;

    // A fresh spawn takes no damage this long, or until it fires.
    private const float SpawnProtection = 1.5f;
    private readonly Dictionary<int, float> _protectedUntil = new();

    // Every this many kills without dying earns a Medi-Shot.
    private const int HealthshotStreak = 3;
    private readonly Dictionary<int, int> _streaks = new();
    private readonly Dictionary<int, float> _healthshotGiven = new();

    public override void Load(bool hotReload)
    {
        Chat.Tag = "DM";
        _loadouts = new LoadoutStore(Path.Combine(ModuleDirectory, "loadouts.json"));

        var words = new ChatWords(this);
        _ = new MapVote(this, words, "dm", Maps, 5, MapEnd.Manual);
        _ = new SiteBanner(this);
        NoHealthshot.Register(this, player =>
            _healthshotGiven.TryGetValue(player.Slot, out var given) && Server.CurrentTime - given < 1f);
        _ = new KillFeedback(this, player => _loadouts.For(player.SteamID).KillSounds);
        _ = new BotWatch(this);

        words.Add(this, "guns", "Choose your weapons", (player, _) => OpenPrimaries(player));
        words.Add(this, "ak", "Play with the AK-47", (player, _) => PickPrimary(player, "weapon_ak47"));
        words.Add(this, "m4", "Play with the M4A4", (player, _) => PickPrimary(player, "weapon_m4a1"));
        words.Add(this, "m4s", "Play with the M4A1-S", (player, _) => PickPrimary(player, "weapon_m4a1_silencer"));
        words.Add(this, "awp", "Play with the AWP", (player, _) => PickPrimary(player, "weapon_awp"));
        words.Add(this, "hs", "Headshots only: your body shots do no damage", (player, _) => ToggleHeadshots(player));
        words.Add(this, "sounds", "Turn the kill sounds on or off", (player, _) => ToggleKillSounds(player));

        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _deadSince.Clear();
            _streaks.Clear();
            ApplyRules();
        });
        // Bots that sat through a hibernation wake up frozen, so the server
        // never sleeps; the shared server.cfg turns it back on every map.
        RegisterListener<Listeners.OnServerHibernationUpdate>(hibernating =>
        {
            if (hibernating)
            {
                Logger.LogWarning("Server went to hibernate; turning sv_hibernate_when_empty off again");
                Server.ExecuteCommand("sv_hibernate_when_empty 0");
            }
        });
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            ApplyRules();
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        RegisterEventHandler<EventItemPurchase>(OnPurchase);
        RegisterEventHandler<EventWeaponFire>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } shooter)
            {
                _protectedUntil.Remove(shooter.Slot);
            }

            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerDeath>(OnDeath);
        RegisterEventHandler<EventPlayerConnectFull>(OnConnect);
        RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        RegisterListener<Listeners.OnEntityTakeDamagePre>(OnTakeDamage);
        AddTimer(0.5f, RespawnDue, TimerFlags.REPEAT);

        var csgo = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "..", ".."));
        _hasBotTree = File.Exists(Path.Combine(csgo, BotTree));

        if (_hasBotTree)
        {
            Logger.LogInformation("Bots use {Tree}", BotTree);
            // The deathmatch cfg puts the game's tree back on every map load,
            // and each bot loads whichever is set when it spawns.
            AddTimer(1f, () => Server.ExecuteCommand($"mp_bot_ai_bt \"{BotTree}\""), TimerFlags.REPEAT);
        }
        else
        {
            Logger.LogWarning("{Tree} is not in {Csgo}: bots keep the game's tree and stall when they try to buy", BotTree, csgo);
        }

        ApplyRules();
    }

    // The game mode's own cfg runs on every map load and may undo the api's
    // cfg; these are the rules the plugin depends on.
    private static void ApplyRules() =>
        Server.ExecuteCommand(string.Join(';', new[]
        {
            "mp_teammates_are_enemies 1",
            "mp_ignore_round_win_conditions 1",
            // The map stays until an admin changes it.
            "mp_timelimit 0",
            "mp_roundtime 60",
            "mp_respawn_on_death_t 1",
            "mp_respawn_on_death_ct 1",
            "mp_randomspawn 1",
            "mp_randomspawn_los 1",
            // The game's immunity puts an INVULNERABILITY box on screen; the
            // plugin protects a fresh spawn silently instead.
            "mp_respawn_immunitytime 0",
            "mp_buy_during_immunity 0",
            "mp_buytime 99999",
            "mp_buy_anywhere 1",
            $"mp_startmoney {Money}",
            $"mp_maxmoney {Money}",
            $"mp_afterroundmoney {Money}",
            "mp_dm_bonus_length_max 0",
            "mp_dm_bonus_length_min 0",
            "mp_dm_time_between_bonus_max 9999",
            "mp_dm_time_between_bonus_min 9999",
            "mp_death_drop_gun 0",
            "mp_weapons_allow_map_placed 0",
            "sv_infinite_ammo 2",
            // What bots spawn with; players' own picks replace it.
            "mp_t_default_primary weapon_ak47",
            "mp_ct_default_primary weapon_m4a1",
            "mp_t_default_secondary weapon_deagle",
            "mp_ct_default_secondary weapon_deagle",
            "mp_free_armor 2",
            $"bot_quota {BotQuota}",
            "bot_quota_mode fill",
            "bot_difficulty 3",
            "bot_join_after_player 0",
            "bot_join_team any",
            "bot_chatter off",
            "mp_autokick 0",
            "sv_hibernate_when_empty 0",
            _hasBotTree ? $"mp_bot_ai_bt \"{BotTree}\"" : "",
        }));

    private static bool OnTeam(CCSPlayerController player) =>
        player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    private static bool IsAlive(CCSPlayerController player) =>
        player.PlayerPawn.Value is { LifeState: (byte)LifeState_t.LIFE_ALIVE };

    private static bool IsFighter(CCSPlayerController? player) =>
        player is { IsValid: true, IsHLTV: false } && (player.IsBot || Players.IsHuman(player));

    private HookResult OnSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is { IsValid: true })
        {
            _protectedUntil[player.Slot] = Server.CurrentTime + SpawnProtection;
        }

        if (player is { IsValid: true, IsBot: true })
        {
            AddTimer(0.1f, () => ArmBot(player), TimerFlags.STOP_ON_MAPCHANGE);
            return HookResult.Continue;
        }

        if (!Players.IsHuman(player))
        {
            return HookResult.Continue;
        }

        _deadSince.Remove(player!.Slot);
        AddTimer(0.1f, () => Equip(player), TimerFlags.STOP_ON_MAPCHANGE);
        FillWallet(player);
        return HookResult.Continue;
    }

    private static void FillWallet(CCSPlayerController player)
    {
        if (!player.IsValid || player.InGameMoneyServices is not { } money || money.Account == Money)
        {
            return;
        }

        money.Account = Money;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    // What a player buys is what they spawn with from then on, like a !guns pick.
    private HookResult OnPurchase(EventItemPurchase @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (!Players.IsHuman(player))
        {
            return HookResult.Continue;
        }

        var item = @event.Weapon.StartsWith("weapon_") ? @event.Weapon : $"weapon_{@event.Weapon}";
        var loadout = _loadouts.For(player!.SteamID);

        if (Weapons.Primaries.Any(weapon => weapon.Item == item))
        {
            loadout.Primary = item;
            _loadouts.Save();
        }
        else if (Weapons.Secondaries.Any(weapon => weapon.Item == item))
        {
            loadout.Secondary = item;
            _loadouts.Save();
        }

        Server.NextFrame(() => FillWallet(player));
        return HookResult.Continue;
    }

    // The game spawns a deathmatch bot with a pistol only. It keeps that and
    // gets a rifle on top; nothing is taken away from it.
    private static void ArmBot(CCSPlayerController bot)
    {
        if (!bot.IsValid || bot.PlayerPawn.Value is not { LifeState: (byte)LifeState_t.LIFE_ALIVE } pawn || pawn.WeaponServices is not { } weapons)
        {
            return;
        }

        var hasRifle = weapons.MyWeapons.Any(handle =>
            handle.Value is { IsValid: true, VData: { } data }
            && new CCSWeaponBaseVData(data.Handle).GearSlot == gear_slot_t.GEAR_SLOT_RIFLE);

        if (!hasRifle)
        {
            bot.GiveNamedItem(BotRifles[Random.Shared.Next(BotRifles.Length)]);
        }
    }

    private void Equip(CCSPlayerController player)
    {
        if (!player.IsValid || !OnTeam(player) || player.PlayerPawn.Value is not { LifeState: (byte)LifeState_t.LIFE_ALIVE } pawn)
        {
            return;
        }

        var loadout = _loadouts.For(player.SteamID);
        player.RemoveWeapons();
        player.GiveNamedItem("weapon_knife");
        player.GiveNamedItem(loadout.Secondary);
        player.GiveNamedItem(loadout.Primary);
        player.GiveNamedItem("item_assaultsuit");
        pawn.Health = 100;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    private HookResult OnDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;

        if (victim is { IsValid: true })
        {
            _streaks.Remove(victim.Slot);
        }

        if (Players.IsHuman(victim))
        {
            _deadSince[victim!.Slot] = Server.CurrentTime;
            AddTimer(RespawnDelay, () => Revive(victim), TimerFlags.STOP_ON_MAPCHANGE);
        }

        if (IsFighter(attacker) && attacker != victim)
        {
            Server.NextFrame(() => Reward(attacker!));
        }

        if (Players.IsHuman(attacker) && attacker != victim)
        {
            var streak = _streaks.GetValueOrDefault(attacker!.Slot) + 1;
            _streaks[attacker.Slot] = streak;

            if (streak % HealthshotStreak == 0)
            {
                Server.NextFrame(() => GiveHealthshot(attacker, streak));
            }
        }

        return HookResult.Continue;
    }

    private void GiveHealthshot(CCSPlayerController player, int streak)
    {
        if (!player.IsValid || !IsAlive(player))
        {
            return;
        }

        _healthshotGiven[player.Slot] = Server.CurrentTime;
        player.GiveNamedItem(NoHealthshot.Healthshot);
        Chat.To(player, $"{ChatColors.Gold}{streak}{ChatColors.Default} kills without dying: here is a {ChatColors.Green}Medi-Shot{ChatColors.Default}.");
    }

    // Health, armour and every magazine back, so the next fight starts even.
    private static void Reward(CCSPlayerController player)
    {
        if (!player.IsValid || player.PlayerPawn.Value is not { LifeState: (byte)LifeState_t.LIFE_ALIVE } pawn)
        {
            return;
        }

        pawn.Health = 100;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        pawn.ArmorValue = 100;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");

        if (pawn.WeaponServices is not { } weapons)
        {
            return;
        }

        foreach (var handle in weapons.MyWeapons)
        {
            if (handle.Value is not { IsValid: true } weapon || weapon.VData is not { } data || data.MaxClip1 <= 0)
            {
                continue;
            }

            weapon.Clip1 = data.MaxClip1;
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_iClip1");
        }
    }

    private static void Revive(CCSPlayerController player)
    {
        if (player.IsValid && OnTeam(player) && !IsAlive(player))
        {
            player.Respawn();
        }
    }

    // Whoever is still dead comes back, wherever the game picks: a player who
    // just joined a side, or one whose respawn on death did not take.
    private void RespawnDue()
    {
        var now = Server.CurrentTime;

        foreach (var player in Players.Humans())
        {
            if (!OnTeam(player) || IsAlive(player))
            {
                _deadSince.Remove(player.Slot);
                continue;
            }

            if (!_deadSince.TryGetValue(player.Slot, out var since))
            {
                _deadSince[player.Slot] = now;
                continue;
            }

            var due = since + RespawnDelay;

            if (now >= due)
            {
                player.Respawn();

                // If the respawn did not take, the next try waits a moment.
                if (now - due > RespawnRetry)
                {
                    _deadSince[player.Slot] = now;
                }
            }
        }
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
            if (!player!.IsValid)
            {
                return;
            }

            Chat.To(player, $"Welcome to {ChatColors.Gold}Deathmatch{ChatColors.Default}: health, armour and ammo back on every kill, a Medi-Shot for every {HealthshotStreak} kills without dying. Buy anything, anywhere: what you buy is what you spawn with.");
            Chat.To(player, $"{ChatColors.Green}!guns{ChatColors.Default} weapons · {ChatColors.Green}!hs{ChatColors.Default} headshots only · {ChatColors.Green}!sounds{ChatColors.Default} kill sounds");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private HookResult OnDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
        {
            _deadSince.Remove(player.Slot);
            _streaks.Remove(player.Slot);
        }

        return HookResult.Continue;
    }

    private void OpenPrimaries(CCSPlayerController player)
    {
        var menu = new ChatMenu("Choose your main weapon");

        foreach (var weapon in Weapons.Primaries)
        {
            menu.AddMenuOption(weapon.Name, (chooser, _) =>
            {
                _loadouts.For(chooser.SteamID).Primary = weapon.Item;
                OpenSecondaries(chooser);
            });
        }

        MenuManager.OpenChatMenu(player, menu);
    }

    private void OpenSecondaries(CCSPlayerController player)
    {
        var menu = new ChatMenu("Choose your pistol");

        foreach (var weapon in Weapons.Secondaries)
        {
            menu.AddMenuOption(weapon.Name, (chooser, _) =>
            {
                var loadout = _loadouts.For(chooser.SteamID);
                loadout.Secondary = weapon.Item;
                _loadouts.Save();
                Chat.To(chooser, $"Your weapons: {ChatColors.Gold}{Weapons.NameOf(loadout.Primary)}{ChatColors.Default} + {ChatColors.Gold}{weapon.Name}");
                Equip(chooser);
            });
        }

        MenuManager.OpenChatMenu(player, menu);
    }

    private void PickPrimary(CCSPlayerController player, string item)
    {
        _loadouts.For(player.SteamID).Primary = item;
        _loadouts.Save();
        Chat.To(player, $"Your main weapon: {ChatColors.Gold}{Weapons.NameOf(item)}");
        Equip(player);
    }

    private void ToggleHeadshots(CCSPlayerController player)
    {
        var loadout = _loadouts.For(player.SteamID);
        loadout.HeadshotsOnly = !loadout.HeadshotsOnly;
        _loadouts.Save();
        Chat.To(player, loadout.HeadshotsOnly
            ? $"Headshots only: {ChatColors.Green}ON{ChatColors.Default}. Only your headshots do damage."
            : $"Headshots only: {ChatColors.LightRed}OFF");
    }

    private void ToggleKillSounds(CCSPlayerController player)
    {
        var loadout = _loadouts.For(player.SteamID);
        loadout.KillSounds = !loadout.KillSounds;
        _loadouts.Save();
        Chat.To(player, loadout.KillSounds
            ? $"Kill sounds: {ChatColors.Green}ON"
            : $"Kill sounds: {ChatColors.LightRed}OFF");
    }

    private HookResult OnTakeDamage(CBaseEntity entity, CTakeDamageInfo info)
    {
        if (entity.DesignerName == "player"
            && new CCSPlayerPawn(entity.Handle).OriginalController.Value is { IsValid: true } victim
            && _protectedUntil.TryGetValue(victim.Slot, out var until)
            && Server.CurrentTime < until)
        {
            return HookResult.Handled;
        }

        if (info.Attacker.Value is not { IsValid: true } attackerEntity || attackerEntity.DesignerName != "player")
        {
            return HookResult.Continue;
        }

        var attacker = new CCSPlayerPawn(attackerEntity.Handle).OriginalController.Value;

        if (!Players.IsHuman(attacker) || !_loadouts.For(attacker!.SteamID).HeadshotsOnly)
        {
            return HookResult.Continue;
        }

        return info.GetHitGroup() == HitGroup_t.HITGROUP_HEAD ? HookResult.Continue : HookResult.Handled;
    }
}
