using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace ServersModes.Duels;

public readonly record struct Spot(float X, float Y, float Z, float Pitch, float Yaw, float Roll)
{
    public float DistanceTo(Spot other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        var dz = Z - other.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public Vector Origin => new(X, Y, Z);

    public QAngle Angles => new(Pitch, Yaw, Roll);
}

public sealed record Arena(List<Spot> T, List<Spot> Ct);

// Arena maps have no notion of an arena: each one is just a T spawn facing a
// CT spawn, far from every other pair. So spawns are matched to their nearest
// opposite spawn, and pairs sitting on top of each other (an arena with more
// than one spawn per side) are folded into one arena.
public static class ArenaFinder
{
    public static List<Arena> Find()
    {
        var t = Spawns("info_player_terrorist");
        var ct = Spawns("info_player_counterterrorist");

        var pairs = new List<(Spot T, Spot Ct, float Distance)>();
        var freeCt = new List<Spot>(ct);

        foreach (var candidate in t
                     .SelectMany(tSpot => ct.Select(ctSpot => (T: tSpot, Ct: ctSpot, Distance: tSpot.DistanceTo(ctSpot))))
                     .OrderBy(pair => pair.Distance))
        {
            if (pairs.Any(pair => pair.T == candidate.T) || !freeCt.Contains(candidate.Ct))
            {
                continue;
            }

            pairs.Add(candidate);
            freeCt.Remove(candidate.Ct);
        }

        if (pairs.Count == 0)
        {
            return new List<Arena>();
        }

        var duelLength = pairs.Select(pair => pair.Distance).OrderBy(distance => distance).ElementAt(pairs.Count / 2);
        var sameArena = duelLength * 0.5f;
        var arenas = new List<Arena>();

        foreach (var pair in pairs)
        {
            var arena = arenas.FirstOrDefault(existing =>
                existing.T.Any(spot => spot.DistanceTo(pair.T) < sameArena)
                && existing.Ct.Any(spot => spot.DistanceTo(pair.Ct) < sameArena)
            );

            if (arena == null)
            {
                arenas.Add(new Arena(new List<Spot> { pair.T }, new List<Spot> { pair.Ct }));
            }
            else
            {
                arena.T.Add(pair.T);
                arena.Ct.Add(pair.Ct);
            }
        }

        return arenas;
    }

    private static List<Spot> Spawns(string designerName) =>
        Utilities
            .FindAllEntitiesByDesignerName<SpawnPoint>(designerName)
            .Where(spawn => spawn.IsValid && spawn.Enabled && spawn.AbsOrigin != null && spawn.AbsRotation != null)
            .Select(spawn => new Spot(
                spawn.AbsOrigin!.X,
                spawn.AbsOrigin.Y,
                spawn.AbsOrigin.Z,
                spawn.AbsRotation!.X,
                spawn.AbsRotation.Y,
                spawn.AbsRotation.Z
            ))
            .Distinct()
            .ToList();
}
