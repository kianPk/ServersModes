using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServersModes.Dm;

public sealed record Weapon(string Name, string Item);

public static class Weapons
{
    public static readonly Weapon[] Primaries =
    [
        new("AK-47", "weapon_ak47"),
        new("M4A4", "weapon_m4a1"),
        new("M4A1-S", "weapon_m4a1_silencer"),
        new("AWP", "weapon_awp"),
        new("SSG 08", "weapon_ssg08"),
        new("Galil AR", "weapon_galilar"),
        new("FAMAS", "weapon_famas"),
        new("AUG", "weapon_aug"),
        new("SG 553", "weapon_sg556"),
        new("MP9", "weapon_mp9"),
        new("MAC-10", "weapon_mac10"),
        new("MP7", "weapon_mp7"),
        new("UMP-45", "weapon_ump45"),
        new("P90", "weapon_p90"),
    ];

    public static readonly Weapon[] Secondaries =
    [
        new("Desert Eagle", "weapon_deagle"),
        new("USP-S", "weapon_usp_silencer"),
        new("Glock-18", "weapon_glock"),
        new("P2000", "weapon_hkp2000"),
        new("P250", "weapon_p250"),
        new("Five-SeveN", "weapon_fiveseven"),
        new("Tec-9", "weapon_tec9"),
        new("CZ75-Auto", "weapon_cz75a"),
        new("Dual Berettas", "weapon_elite"),
        new("R8 Revolver", "weapon_revolver"),
    ];

    public static string NameOf(string item) =>
        Primaries.Concat(Secondaries).FirstOrDefault(weapon => weapon.Item == item)?.Name ?? item;
}

public enum RespawnSpeed
{
    Fast,
    Medium,
    Slow,
}

public sealed class Loadout
{
    public string Primary { get; set; } = "weapon_ak47";
    public string Secondary { get; set; } = "weapon_deagle";
    public RespawnSpeed Respawn { get; set; } = RespawnSpeed.Medium;
    public bool HeadshotsOnly { get; set; }
    public bool KillSounds { get; set; } = true;
}

// Kept on the server's disk: a mode server has no database, and losing it with
// the pod costs a player one !guns.
public sealed class LoadoutStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Dictionary<ulong, Loadout> _players;

    public LoadoutStore(string path)
    {
        _path = path;

        try
        {
            _players = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<ulong, Loadout>>(File.ReadAllText(path), Json) ?? new()
                : new();
        }
        catch (Exception)
        {
            _players = new();
        }
    }

    public Loadout For(ulong steamId)
    {
        if (!_players.TryGetValue(steamId, out var loadout))
        {
            loadout = new Loadout();
            _players[steamId] = loadout;
        }

        return loadout;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_players, Json));
        }
        catch (Exception)
        {
            // A read-only plugin directory only costs persistence across restarts.
        }
    }
}
