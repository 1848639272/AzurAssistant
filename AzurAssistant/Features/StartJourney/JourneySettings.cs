namespace AzurAssistant.Features.StartJourney;

public sealed record JourneySettings
{
    public int SchemaVersion { get; init; } = 4;
    public int ReferenceWidth { get; init; } = 1920;
    public int ReferenceHeight { get; init; } = 1080;
    public int[] AgeSearch { get; init; } = [24, 930, 108, 128];
    public double AgeThreshold { get; init; } = 0.90;
    public int[] StartClick { get; init; } = [966, 903];
    public string AgeSha256 { get; init; } = "";
    public void Validate()
    {
        if (SchemaVersion != 4 || ReferenceWidth != 1920 || ReferenceHeight != 1080 || !double.IsFinite(AgeThreshold) || AgeThreshold is < 0.5 or > 1
            || AgeSearch is null || AgeSearch.Length != 4 || StartClick is null || StartClick.Length != 2
            || AgeSha256 is null || AgeSha256.Length != 64 || !AgeSha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("进入游戏需要 SchemaVersion=4 适龄标识资源包及 1920×1080 参考分辨率。");
        if (StartClick[0] < 0 || StartClick[1] < 0 || StartClick[0] >= ReferenceWidth || StartClick[1] >= ReferenceHeight)
            throw new InvalidOperationException("开始旅程点击位置超出参考画面。");
        foreach (var rect in new[] { AgeSearch })
            if (rect[0] < 0 || rect[1] < 0 || rect[2] <= 0 || rect[3] <= 0 || rect[0] + rect[2] > ReferenceWidth || rect[1] + rect[3] > ReferenceHeight)
                throw new InvalidOperationException("开始旅程资源区域超出参考画面。");
    }
}
