using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServersModes.Duels;

public enum RoundType
{
    Rifle,
    Awp,
    Scout,
    Pistol,
}

public sealed record Weapon(string Name, string Item);

public static class Weapons
{
    public static readonly Weapon[] Rifles =
    [
        new("AK-47", "weapon_ak47"),
        new("M4A4", "weapon_m4a1"),
        new("M4A1-S", "weapon_m4a1_silencer"),
        new("Galil AR", "weapon_galilar"),
        new("FAMAS", "weapon_famas"),
        new("AUG", "weapon_aug"),
        new("SG 553", "weapon_sg556"),
    ];

    public static readonly Weapon[] Pistols =
    [
        new("Desert Eagle", "weapon_deagle"),
        new("USP-S", "weapon_usp_silencer"),
        new("Glock-18", "weapon_glock"),
        new("P250", "weapon_p250"),
        new("Five-SeveN", "weapon_fiveseven"),
        new("Tec-9", "weapon_tec9"),
        new("CZ75-Auto", "weapon_cz75a"),
        new("Dual Berettas", "weapon_elite"),
        new("R8 Revolver", "weapon_revolver"),
    ];

    public static string RoundName(RoundType round) =>
        round switch
        {
            RoundType.Rifle => "Rifle",
            RoundType.Awp => "AWP",
            RoundType.Scout => "Scout",
            _ => "Pistol",
        };
}

public sealed class PlayerPreferences
{
    public string Rifle { get; set; } = "weapon_ak47";
    public string Pistol { get; set; } = "weapon_deagle";
    public List<RoundType> Rounds { get; set; } = Enum.GetValues<RoundType>().ToList();
}

// Kept on the server's disk rather than in a database: a mode server has none,
// and losing preferences with the pod costs a player one !guns.
public sealed class PreferenceStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Dictionary<ulong, PlayerPreferences> _players;

    public PreferenceStore(string path)
    {
        _path = path;

        try
        {
            _players = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<ulong, PlayerPreferences>>(File.ReadAllText(path), Json) ?? new()
                : new();
        }
        catch (Exception)
        {
            _players = new();
        }
    }

    public PlayerPreferences For(ulong steamId)
    {
        if (!_players.TryGetValue(steamId, out var preferences))
        {
            preferences = new PlayerPreferences();
            _players[steamId] = preferences;
        }

        if (preferences.Rounds.Count == 0)
        {
            preferences.Rounds = Enum.GetValues<RoundType>().ToList();
        }

        return preferences;
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
