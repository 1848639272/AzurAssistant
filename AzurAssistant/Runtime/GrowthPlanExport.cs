using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzurAssistant.Runtime;

/// <summary>Exports the current growth plan as CSV or as a PNG summary card.</summary>
public static class GrowthPlanExport
{
    public sealed record PlanSnapshot(string Title, string Summary, string Crystals, string Stamina,
        IReadOnlyList<GrowthMaterialRow> Materials, string PotentialText, string Stamp);

    public static string ToCsv(PlanSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("材料,需要,拥有,缺口,体力估算");
        foreach (var row in snapshot.Materials)
            builder.AppendLine(string.Join(',',
                Quote(row.Name), row.Need.ToString(CultureInfo.InvariantCulture), row.Owned.ToString(CultureInfo.InvariantCulture),
                row.Missing.ToString(CultureInfo.InvariantCulture), Quote(row.StaminaText)));
        builder.AppendLine();
        builder.AppendLine(Quote("对象") + "," + Quote(snapshot.Title));
        builder.AppendLine(Quote("摘要") + "," + Quote(snapshot.Summary));
        if (snapshot.Crystals.Length > 0) builder.AppendLine(Quote("星脉结晶") + "," + Quote(snapshot.Crystals));
        if (snapshot.PotentialText.Length > 0) builder.AppendLine(Quote("潜能") + "," + Quote(snapshot.PotentialText));
        builder.AppendLine(Quote("体力") + "," + Quote(snapshot.Stamina));
        builder.AppendLine(Quote("生成时间") + "," + Quote(snapshot.Stamp));
        return builder.ToString();
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>Renders the plan to a PNG card (1280×auto): title, summary, and the material table.</summary>
    public static BitmapSource ToPng(PlanSnapshot snapshot)
    {
        const double width = 1280;
        const double margin = 40;
        const double rowHeight = 40;
        var rows = snapshot.Materials.Count;
        var header = snapshot.PotentialText.Length > 0 ? 250 : 215;
        var height = header + Math.Max(1, rows) * rowHeight + 130;

        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFD)), null, new Rect(0, 0, width, height));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x17, 0x8C, 0x84)), null, new Rect(0, 0, width, 6));

            Text(context, "养成方案", bold, 34, Brushes.Black, margin, 34, width - margin * 2);
            Text(context, snapshot.Title, typeface, 20, new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x55)), margin, 92, width - margin * 2);
            Text(context, snapshot.Summary, typeface, 16, new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x2E)), margin, 132, width - margin * 2);
            var y = 176.0;
            if (snapshot.Crystals.Length > 0)
            {
                Text(context, "星脉结晶：" + snapshot.Crystals, typeface, 15, new SolidColorBrush(Color.FromRgb(0x55, 0x5B, 0x66)), margin, y, width - margin * 2);
                y += 28;
            }
            if (snapshot.PotentialText.Length > 0)
            {
                Text(context, snapshot.PotentialText, typeface, 15, new SolidColorBrush(Color.FromRgb(0x55, 0x5B, 0x66)), margin, y, width - margin * 2);
                y += 28;
            }
            if (snapshot.Stamina.Length > 0)
            {
                Text(context, snapshot.Stamina, typeface, 15, new SolidColorBrush(Color.FromRgb(0x17, 0x6B, 0x64)), margin, y, width - margin * 2);
                y += 28;
            }

            var tableTop = header;
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xEC, 0xF1, 0xF4)), null, new Rect(margin, tableTop - 8, width - margin * 2, 32));
            Text(context, "材料", bold, 15, Brushes.Black, margin + 12, tableTop - 2, 320);
            Text(context, "需要", bold, 15, Brushes.Black, margin + 360, tableTop - 2, 120);
            Text(context, "拥有", bold, 15, Brushes.Black, margin + 500, tableTop - 2, 120);
            Text(context, "缺口", bold, 15, Brushes.Black, margin + 640, tableTop - 2, 120);
            Text(context, "体力估算", bold, 15, Brushes.Black, margin + 800, tableTop - 2, 340);

            var rowY = (double)tableTop + 30;
            var alternate = false;
            foreach (var row in snapshot.Materials)
            {
                if (alternate)
                    context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF6, 0xF9)), null,
                        new Rect(margin, rowY, width - margin * 2, rowHeight));
                alternate = !alternate;
                Text(context, row.Name, typeface, 15, Brushes.Black, margin + 12, rowY + 8, 330);
                Text(context, row.Need.ToString("N0", CultureInfo.CurrentCulture), typeface, 15, Brushes.Black, margin + 360, rowY + 8, 120);
                Text(context, row.Owned.ToString("N0", CultureInfo.CurrentCulture), typeface, 15, Brushes.Black, margin + 500, rowY + 8, 120);
                Text(context, row.Missing.ToString("N0", CultureInfo.CurrentCulture), typeface, 15,
                    row.Missing > 0 ? new SolidColorBrush(Color.FromRgb(0xB0, 0x3A, 0x2B)) : new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
                    margin + 640, rowY + 8, 120);
                Text(context, row.StaminaText, typeface, 14, new SolidColorBrush(Color.FromRgb(0x55, 0x5B, 0x66)), margin + 800, rowY + 8, 380);
                rowY += rowHeight;
            }

            Text(context, "由蔚蓝助手生成 · " + snapshot.Stamp, typeface, 13, new SolidColorBrush(Color.FromRgb(0x88, 0x8E, 0x99)),
                margin, height - 46, width - margin * 2);
        }

        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Text(DrawingContext context, string text, Typeface typeface, double size, Brush brush,
        double x, double y, double maxWidth)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush,
            1.0) { MaxTextWidth = Math.Max(40, maxWidth), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        context.DrawText(formatted, new Point(x, y));
    }

    public static string ToCsvFile(string path, PlanSnapshot snapshot)
    {
        File.WriteAllText(path, ToCsv(snapshot), new UTF8Encoding(true));
        return path;
    }

    public static string ToPngFile(string path, PlanSnapshot snapshot)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToPng(snapshot)));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
