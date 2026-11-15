namespace CloudLauncher.Shared;

/// <summary>One point of a stroke drawn on a planning board, in board units.</summary>
/// <param name="Pressure">0 to 1, as the pen reported it; 0.5 is the stroke's own width (the mouse and
/// touch always draw at 0.5). WPF draws a point <c>1.5 x pressure + 0.25</c> times the width, so a
/// light touch is a quarter of it and a firm press 1.75 times.</param>
public readonly record struct InkPoint(double X, double Y, float Pressure);

/// <summary>
/// Packs a drawn stroke's points into the short string a board stores (<c>PlanStroke.Points</c> in the
/// launcher), and back. The launcher and the server's board pictures share it.
/// </summary>
/// <remarks>
/// <para>Base64 of: a format byte (1), then for each point its X and Y in tenths of a board unit and
/// its pressure in 255ths, the first point as it is and every later one as the difference from the
/// point before, each as a zigzag varint. A pen moving a few pixels between samples takes three to
/// four bytes a point, about a fifth of the same points written out as JSON numbers, which matters
/// because the whole board travels to the server on every edit.</para>
/// <para>Decoding never throws: anything it can't read gives no points, so one damaged stroke can't
/// stop a board from opening.</para>
/// </remarks>
public static class PlanInkCodec
{
    private const byte Format = 1;

    /// <summary>More points than any stroke a person draws. Decoding stops there, so a hostile or
    /// broken string can't make the server allocate without end.</summary>
    public const int MaxPoints = 100_000;

    public static string Encode(IReadOnlyList<InkPoint> points)
    {
        if (points.Count == 0) return "";
        var bytes = new List<byte>(points.Count * 4 + 1) { Format };
        long px = 0, py = 0, pp = 0;
        foreach (var p in points)
        {
            long x = Quantize(p.X), y = Quantize(p.Y);
            long pr = (long)Math.Round(Math.Clamp(float.IsFinite(p.Pressure) ? p.Pressure : 0.5f, 0f, 1f) * 255);
            Write(bytes, x - px);
            Write(bytes, y - py);
            Write(bytes, pr - pp);
            px = x; py = y; pp = pr;
        }
        return Convert.ToBase64String(bytes.ToArray());
    }

    public static List<InkPoint> Decode(string? data)
    {
        var points = new List<InkPoint>();
        if (string.IsNullOrEmpty(data)) return points;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { return points; }
        if (bytes.Length == 0 || bytes[0] != Format) return points;

        var at = 1;
        long x = 0, y = 0, pr = 0;
        while (at < bytes.Length && points.Count < MaxPoints)
        {
            if (!Read(bytes, ref at, out var dx) || !Read(bytes, ref at, out var dy) || !Read(bytes, ref at, out var dp))
                break; // a cut-off tail: keep the points that were whole
            x += dx; y += dy; pr += dp;
            points.Add(new InkPoint(x / 10.0, y / 10.0, (float)(Math.Clamp(pr, 0, 255) / 255.0)));
        }
        return points;
    }

    private static long Quantize(double v) =>
        double.IsFinite(v) ? (long)Math.Round(Math.Clamp(v, -1e9, 1e9) * 10) : 0;

    private static void Write(List<byte> to, long value)
    {
        var u = (ulong)((value << 1) ^ (value >> 63)); // zigzag: small magnitudes either way stay small
        while (u >= 0x80)
        {
            to.Add((byte)(u | 0x80));
            u >>= 7;
        }
        to.Add((byte)u);
    }

    private static bool Read(byte[] from, ref int at, out long value)
    {
        value = 0;
        ulong u = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (at >= from.Length) return false;
            var b = from[at++];
            u |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                value = (long)(u >> 1) ^ -(long)(u & 1);
                return true;
            }
        }
        return false;
    }

    /// <summary>The width a point is drawn at, as WPF's ink renderer works it out.</summary>
    public static double PressureScale(float pressure) => 1.5 * Math.Clamp(pressure, 0f, 1f) + 0.25;
}
