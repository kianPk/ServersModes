using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace ServersModes.Duels;

// The site's address in every arena: one sign over each spawn, turned to the
// spawn across from it, so each duellist reads it just above their opponent.
public sealed class ArenaSigns
{
    private const string Text = "yguard.ir";

    // Over a standing player's head (eyes are at 64).
    private const float Height = 110f;

    private readonly List<CPointWorldText> _signs = new();

    public int Place(IEnumerable<Arena> arenas)
    {
        Clear();

        foreach (var arena in arenas)
        {
            Add(over: arena.Ct[0], facing: arena.T[0]);
            Add(over: arena.T[0], facing: arena.Ct[0]);
        }

        return _signs.Count;
    }

    public void Clear()
    {
        foreach (var sign in _signs.Where(sign => sign.IsValid))
        {
            sign.Remove();
        }

        _signs.Clear();
    }

    private void Add(Spot over, Spot facing)
    {
        var sign = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");

        if (sign == null)
        {
            return;
        }

        sign.MessageText = Text;
        sign.Enabled = true;
        sign.Fullbright = true;
        // Without a font the text is never drawn.
        sign.FontName = "Arial Bold";
        sign.FontSize = 100;
        sign.WorldUnitsPerPx = 0.25f;
        sign.DepthOffset = 0f;
        sign.DrawBackground = false;
        sign.Color = Color.FromArgb(255, 245, 165, 36);
        sign.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
        sign.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;
        // Each client turns it to face them: the text is one-sided.
        sign.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_AROUND_UP;

        // A world text's face looks down its yaw - 90, rolled upright.
        var towardSign = MathF.Atan2(over.Y - facing.Y, over.X - facing.X) * 180f / MathF.PI;
        sign.Teleport(new Vector(over.X, over.Y, over.Z + Height), new QAngle(0, towardSign + 270f, 90f), new Vector(0, 0, 0));
        sign.DispatchSpawn();

        _signs.Add(sign);
    }
}
