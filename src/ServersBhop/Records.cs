using System.Text.Json;

namespace ServersModes.Bhop;

public sealed class Record
{
    public string Name { get; set; } = "";
    public double Time { get; set; }
    public DateTime At { get; set; }
}

public enum Finish
{
    Slower,
    PersonalBest,
    WorldRecord,
}

// Best times per map and track ("main", "b1", "b2" …), on the server's disk:
// a mode server has no database of its own.
public sealed class RecordStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<string, Dictionary<string, Dictionary<ulong, Record>>> _maps;

    public RecordStore(string path)
    {
        _path = path;

        try
        {
            _maps = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<ulong, Record>>>>(File.ReadAllText(path), Json) ?? new()
                : new();
        }
        catch (Exception)
        {
            _maps = new();
        }
    }

    public static string Track(int bonus) => bonus == 0 ? "main" : $"b{bonus}";

    private Dictionary<ulong, Record> Times(string map, string track)
    {
        if (!_maps.TryGetValue(map, out var tracks))
        {
            tracks = new();
            _maps[map] = tracks;
        }

        if (!tracks.TryGetValue(track, out var times))
        {
            times = new();
            tracks[track] = times;
        }

        return times;
    }

    public List<(ulong SteamId, Record Record)> Top(string map, string track, int count) =>
        Times(map, track)
            .OrderBy(entry => entry.Value.Time)
            .Take(count)
            .Select(entry => (entry.Key, entry.Value))
            .ToList();

    public Record? Best(string map, string track, ulong steamId) =>
        Times(map, track).GetValueOrDefault(steamId);

    public int Rank(string map, string track, ulong steamId)
    {
        var times = Times(map, track);
        return times.TryGetValue(steamId, out var mine) ? times.Values.Count(other => other.Time < mine.Time) + 1 : 0;
    }

    public int Count(string map, string track) => Times(map, track).Count;

    public Finish Submit(string map, string track, ulong steamId, string name, double time)
    {
        var times = Times(map, track);
        var record = times.Count > 0 ? times.Values.Min(entry => entry.Time) : double.MaxValue;

        if (times.TryGetValue(steamId, out var mine) && mine.Time <= time)
        {
            mine.Name = name;
            return Finish.Slower;
        }

        times[steamId] = new Record { Name = name, Time = time, At = DateTime.UtcNow };
        Save();
        return time < record ? Finish.WorldRecord : Finish.PersonalBest;
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_maps, Json));
        }
        catch (Exception)
        {
            // A read-only plugin directory only costs persistence across restarts.
        }
    }

    public static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds:000}"
            : $"{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds:000}";
    }
}
