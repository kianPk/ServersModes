using System.Text.Json;
using CounterStrikeSharp.API.Modules.Utils;

namespace ServersModes.Bhop;

// A zone a map does not mark with a trigger: two opposite corners taken at
// foot level, reaching up past a jump so hopping in place stays inside.
public sealed class Box
{
    private const float Height = 72f;
    private const float Below = 2f;

    public float[] A { get; set; } = new float[3];
    public float[] B { get; set; } = new float[3];

    public static Box Of(float ax, float ay, float az, float bx, float by, float bz) =>
        new() { A = [ax, ay, az], B = [bx, by, bz] };

    public bool Contains(Vector point) =>
        point.X >= Math.Min(A[0], B[0]) && point.X <= Math.Max(A[0], B[0])
        && point.Y >= Math.Min(A[1], B[1]) && point.Y <= Math.Max(A[1], B[1])
        && point.Z >= Math.Min(A[2], B[2]) - Below && point.Z <= Math.Max(A[2], B[2]) + Height;

    public Vector Floor() => new((A[0] + B[0]) / 2, (A[1] + B[1]) / 2, Math.Min(A[2], B[2]) + Below);
}

public sealed class MapBoxes
{
    public Box? Start { get; set; }
    public Box? End { get; set; }
    public float[]? Respawn { get; set; }

    public bool Complete => Start != null && End != null;

    public Vector Spawn() => Respawn is [var x, var y, var z] ? new Vector(x, y, z) : Start!.Floor();
}

// Box zones per map, on the server's disk next to the records. A map saved
// here overrides the built-in boxes, and a cleared one (saved empty) falls
// back to the map's own triggers.
public sealed class BoxStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // The rotation's maps that ship without timer triggers; corners from
    // SharpTimer's MapData.
    private static readonly Dictionary<string, MapBoxes> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bhop_emevaelx3"] = new()
        {
            Start = Box.Of(-14755.482f, -13946.281f, 64.03125f, -14999.712f, -13704.743f, 64.03125f),
            End = Box.Of(-5715.069f, -1391.9689f, -1375.9688f, -6248.439f, -1008.04407f, -1375.9688f),
            Respawn = [-14865.554f, -13795.172f, 64.03125f],
        },
        ["bhop_cherryblossom"] = new()
        {
            Start = Box.Of(1613.6593f, 311.9121f, 48.03125f, 2040.6968f, -124.99468f, 48.03125f),
            End = Box.Of(3940.031f, -8171.247f, 48.03125f, 4378.91f, -8616.119f, 48.031242f),
            Respawn = [1844.5771f, 106.57258f, 48.031246f],
        },
    };

    private readonly string _path;
    private readonly Dictionary<string, MapBoxes> _maps;

    public BoxStore(string path)
    {
        _path = path;

        try
        {
            _maps = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, MapBoxes>>(File.ReadAllText(path), Json) ?? new()
                : new();
        }
        catch (Exception)
        {
            _maps = new();
        }
    }

    // The map name is still empty while the server boots.
    public MapBoxes? For(string? map) =>
        string.IsNullOrEmpty(map) ? null
        : _maps.TryGetValue(map, out var saved) ? saved
        : BuiltIn.GetValueOrDefault(map);

    public void SetStart(string map, Box box)
    {
        var boxes = Editable(map);
        boxes.Start = box;
        boxes.Respawn = null;
        Save();
    }

    public void SetEnd(string map, Box box)
    {
        Editable(map).End = box;
        Save();
    }

    public void Clear(string map)
    {
        _maps[map] = new MapBoxes();
        Save();
    }

    private MapBoxes Editable(string map)
    {
        if (!_maps.TryGetValue(map, out var boxes))
        {
            var builtIn = BuiltIn.GetValueOrDefault(map);
            boxes = new MapBoxes { Start = builtIn?.Start, End = builtIn?.End, Respawn = builtIn?.Respawn };
            _maps[map] = boxes;
        }

        return boxes;
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
}
