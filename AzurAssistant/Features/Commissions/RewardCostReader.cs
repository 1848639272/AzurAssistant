using System.Globalization;
using System.Text.RegularExpressions;
using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Features.Commissions;

internal sealed record RewardCostCandidate(string Source, int? Value, string Reason, string Evidence);
internal sealed record RewardCostReading(int? Value, bool Conflict, IReadOnlyList<RewardCostCandidate> Candidates)
{
    public string Diagnostic => "reward-cost-reading=" + (Conflict ? "conflict" : Value?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
        + "[" + string.Join(";", Candidates.Select(c => $"{c.Source}:{c.Reason}:{c.Evidence}")) + "]";
}

/// <summary>Interprets the displayed cost; neither inventory nor the selected multiplier supplies numeric evidence.</summary>
internal static class RewardCostReader
{
    private const double MinimumCharacterScore = .80;
    private const string Label = "取宝箱需要消耗";

    public static async Task<RewardCostReading> ReadAsync(PpOcrReader ocr, FrameSnapshot frame,
        PixelRegion sentenceRegion, PixelRegion numberRegion, CancellationToken token)
    {
        var candidates = new List<RewardCostCandidate>();
        // Two complementary crops share the same immutable frame. A second scale is bounded fallback,
        // not a retry of the resource action or a search until some value happens to pass.
        foreach (var scale in new[] { 2, 3 })
        {
            candidates.Add(Evaluate(await ocr.ReadAsync(frame, token, sentenceRegion, scale), sentenceRegion,
                numberRegion, true, "sentence-x" + scale));
            candidates.Add(Evaluate(await ocr.ReadAsync(frame, token, numberRegion, scale), numberRegion,
                numberRegion, false, "number-x" + scale));
            var reading = Resolve(candidates);
            if (reading.Value is not null || reading.Conflict) return reading;
        }
        return Resolve(candidates);
    }

    internal static RewardCostReading Resolve(IEnumerable<RewardCostCandidate> candidates)
    {
        var values = candidates.ToArray();
        var distinct = values.Where(c => c.Value is not null).Select(c => c.Value!.Value).Distinct().ToArray();
        return new(distinct.Length == 1 ? distinct[0] : null, distinct.Length > 1, values);
    }

    internal static RewardCostCandidate Evaluate(IEnumerable<TextRegion> words, PixelRegion region,
        PixelRegion numberRegion, bool sentence, string source)
    {
        var blocks = words.OrderBy(w => w.X).ToArray();
        var evidence = string.Join("/", blocks.Select(w => $"'{w.Text.Replace("\n", " ").Replace("\r", " ")}' block={w.Confidence:F3}"));
        RewardCostCandidate Reject(string reason) => new(source, null, reason, evidence);
        if (blocks.Length == 0) return Reject("missing");
        if (blocks.Length > 3) return Reject("ambiguous-blocks");
        // OCR boxes may touch the padded crop's left/top edge. The right edge must leave room for
        // the complete number; a cropped suffix must not turn 120 into a plausible 12.
        if (blocks.Any(w => !double.IsFinite(w.X + w.Y + w.Width + w.Height) || w.Width <= 0 || w.Height <= 0
            || w.X < region.X || w.Y < region.Y || w.X + w.Width >= region.X + region.Width - 1
            || w.Y + w.Height >= region.Y + region.Height - 1)) return Reject("clipped-or-outside");
        var center = blocks[0].Y + blocks[0].Height / 2;
        if (blocks.Any(w => Math.Abs(w.Y + w.Height / 2 - center) > Math.Min(w.Height, blocks[0].Height) / 2))
            return Reject("different-lines");
        for (var i = 1; i < blocks.Length; i++)
            if (blocks[i].X < blocks[i - 1].X + blocks[i - 1].Width - 2
                || blocks[i].X - (blocks[i - 1].X + blocks[i - 1].Width) > blocks[i].Height * 2)
                return Reject("overlapping-or-separated");
        if (blocks.Any(w => w.CharacterConfidences is not { } scores || scores.Count != w.Text.Length))
            return Reject("unaligned-character-scores");
        if (blocks.Any(w => w.CharacterConfidences!.Any(s => !float.IsFinite(s) || s < 0 || s > 1)))
            return Reject("invalid-character-scores");

        var text = string.Concat(blocks.Select(w => w.Text));
        var pattern = sentence ? @"^\s*" + Label + @"\s*([1-9][0-9]{0,2})\s*[)）]?\s*$"
            : @"^\s*([1-9][0-9]{0,2})\s*[)）]?\s*$";
        var match = Regex.Match(text, pattern);
        if (!match.Success) return Reject("incomplete-field");
        // Splitting punctuation or label/number is harmless. Splitting digits is ambiguous.
        var numericBlocks = blocks.Where(w => w.Text.Any(char.IsAsciiDigit)).ToArray();
        if (numericBlocks.Length != 1 || numericBlocks[0].X + numericBlocks[0].Width <= numberRegion.X)
            return Reject("ambiguous-number-position");
        var scoresByCharacter = blocks.SelectMany(w => w.CharacterConfidences!).ToArray();
        var digits = match.Groups[1];
        var digitScores = scoresByCharacter.Skip(digits.Index).Take(digits.Length).ToArray();
        var minimum = digitScores.Min();
        evidence += $" digits-min={minimum:F3}";
        if (digitScores.Any(s => !float.IsFinite(s) || s < MinimumCharacterScore || s > 1)) return Reject("weak-digits");
        if (sentence && text.Select((c, i) => (c, i)).Where(x => char.IsLetter(x.c))
            .Any(x => !float.IsFinite(scoresByCharacter[x.i]) || scoresByCharacter[x.i] < MinimumCharacterScore || scoresByCharacter[x.i] > 1))
            return Reject("weak-label");
        return new(source, int.Parse(digits.Value, CultureInfo.InvariantCulture), "accepted", evidence);
    }
}
