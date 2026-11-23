using System.Globalization;
using System.Text;

namespace AzurAssistant.Runtime;

/// <summary>One cover rectangle in 1920×1080 reference space.</summary>
public readonly record struct CoverBox(int X, int Y, int Width, int Height)
{
    public static CoverBox Default => new(UidCoverGeometry.DefaultX, UidCoverGeometry.DefaultY,
        UidCoverGeometry.DefaultWidth, UidCoverGeometry.DefaultHeight);

    public string Describe => $"{X},{Y} {Width}×{Height}";
}

/// <summary>Parses and formats the cover box list ("78,1026,132,26; 0,0,100,20") with bounds.</summary>
public static class CoverBoxes
{
    public const int MaxBoxes = 8;
    public const int MaxReference = 8192;

    public static IReadOnlyList<CoverBox> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var boxes = new List<CoverBox>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var numbers = part.Split(',', StringSplitOptions.TrimEntries);
            if (numbers.Length != 4) continue;
            if (!int.TryParse(numbers[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(numbers[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                || !int.TryParse(numbers[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
                || !int.TryParse(numbers[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) continue;
            boxes.Add(Clamp(x, y, w, h));
            if (boxes.Count >= MaxBoxes) break;
        }
        return boxes;
    }

    public static string Format(IEnumerable<CoverBox> boxes) =>
        string.Join(";", boxes.Take(MaxBoxes).Select(box => $"{box.X},{box.Y},{box.Width},{box.Height}"));

    public static CoverBox Clamp(int x, int y, int width, int height) => new(
        Math.Clamp(x, 0, MaxReference - 1),
        Math.Clamp(y, 0, MaxReference - 1),
        Math.Clamp(width, 1, MaxReference),
        Math.Clamp(height, 1, MaxReference));

    /// <summary>The configured boxes, or the legacy single rectangle when no list was saved yet.</summary>
    public static IReadOnlyList<CoverBox> Resolve(string? text, int legacyX, int legacyY, int legacyWidth, int legacyHeight)
    {
        var parsed = Parse(text);
        return parsed.Count > 0 ? parsed : [Clamp(legacyX, legacyY, legacyWidth, legacyHeight)];
    }
}

/// <summary>How the cover paints the hidden area.</summary>
public static class CoverStyles
{
    public const string Solid = "solid";
    public const string Mosaic = "mosaic";

    public static readonly string[] All = [Solid, Mosaic];

    public static string Normalize(string? style) =>
        string.Equals(style, Mosaic, StringComparison.OrdinalIgnoreCase) ? Mosaic : Solid;
}
