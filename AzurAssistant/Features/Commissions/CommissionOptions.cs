using System.IO;
using System.Text.Json;

namespace AzurAssistant.Features.Commissions;

public enum CommissionKind { Daily, Weekly, Crisis, Story }
public sealed record DungeonChoice(string Name, string Category, int MaxDifficulty, int MinDifficulty = 1);
public sealed record StoryChapterChoice(int Number, string Name);

public sealed record CommissionOptions
{
    public int SchemaVersion { get; init; } = 3;
    public string DailyDungeon { get; init; } = "银光闪闪";
    public int DailyDifficulty { get; init; } = 1;
    public string WeeklyDungeon { get; init; } = "暗焰兽";
    public int WeeklyDifficulty { get; init; } = 1;
    public string TeamName { get; init; } = "";
    public int StoryChapter { get; init; } = 1;
    public string StoryDungeon { get; init; } = "1-1 离故土";
    public string StoryTeamName { get; init; } = "";
    public bool DailyClaimLimitEnabled { get; init; }
    public int DailyClaimLimit { get; init; } = 5;
    public bool WeeklyClaimLimitEnabled { get; init; }
    public int WeeklyClaimLimit { get; init; } = 5;
    public bool CrisisSpecialClaimLimitEnabled { get; init; }
    public int CrisisSpecialClaimLimit { get; init; } = 5;
    public bool CrisisNormalClaimLimitEnabled { get; init; }
    public int CrisisNormalClaimLimit { get; init; } = 5;
    public bool StoryClaimLimitEnabled { get; init; }
    public int StoryClaimLimit { get; init; } = 5;
    public int CrisisDifficulty { get; init; } = 6;
    public string CrisisNormal { get; init; } = "顺便采点草";
    public bool CrisisNormalEnabled { get; init; } = true;
    public bool CrisisSpecialEnabled { get; init; }
    public string CrisisSpecial { get; init; } = "拿它们热身";

    public static IReadOnlyList<DungeonChoice> DailyChoices { get; } =
        new[] { "银光闪闪", "结晶萃取", "千锤百炼" }.Select(n => new DungeonChoice(n, "基础材料", 6))
        .Concat(new[] { "无惧之战砧", "凋零的挽歌", "温和的雷鸣", "深巢梦魇", "林间幻梦", "吞噬之渊", "焚灼之域" }.Select(n => new DungeonChoice(n, "首领挑战", 6)))
        .Concat(new[] { "苍雷之卫", "烈炎之佑", "常青之庇", "湍流之守", "丘薮之陲", "长风之护", "厚岩之盾", "严寒之屏", "日月之捍", "急炽之御" }
            .Select((n, i) => new DungeonChoice(n, "武备获取", 5, i < 2 ? 1 : i < 5 ? 2 : 4))).ToArray();
    public static IReadOnlyList<string> WeeklyChoices { get; } = ["暗焰兽", "双翼之舞"];
    public static IReadOnlyList<StoryChapterChoice> StoryChapters { get; } = Array.AsReadOnly<StoryChapterChoice>([
        new(1, "Ⅰ · 萨满手札"), new(2, "Ⅱ · 祖赞卡神话"), new(3, "Ⅲ · 狼神传说"), new(4, "Ⅳ · 狂风将至")]);
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<string>> StoryByChapter =
        new Dictionary<int, IReadOnlyList<string>>
        {
            [1] = Array.AsReadOnly(new[] { "1-1 离故土", "1-2 寻生机", "1-3 涉万里", "1-4 居陌谷", "1-5 衍子民", "1-6 御灾陨", "1-7 惘百约", "1-8 逢星临" }),
            [2] = Array.AsReadOnly(new[] { "2-1 天地混沌", "2-2 七灵初生", "2-3 世界以成", "2-4 万灵懵懂", "2-5 灾厄环伺", "2-6 鏖战求生", "2-7 铭继图腾" }),
            [3] = Array.AsReadOnly(new[] { "3-1 银河星途", "3-2 眸光所向", "3-3 无岸之桥", "3-4 前路茫茫", "3-5 悠长回声", "3-6 诚祈庇护", "3-7 狼神守望" }),
            [4] = Array.AsReadOnly(new[] { "4-1 守新土", "4-2 祭己身", "4-3 镇狂灵", "4-4 轮回世", "4-5 重相逢", "4-6 杀机起", "4-7 生意灭", "4-8 联异族", "4-9 合众心", "4-10 除狂厄" })
        };
    public static IReadOnlyList<string> GetStoryChoices(int chapter)
        => StoryByChapter.GetValueOrDefault(chapter) ?? Array.Empty<string>();
    // Supplied 2026-10-03 crisis screenshots 3.1–3.7 define separate lists per tier.
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<string>> CrisisByDifficulty =
        new Dictionary<int, IReadOnlyList<string>>
        {
            [1] = Array.AsReadOnly(new[] { "声音太吵了", "危险的运输路线" }),
            [2] = Array.AsReadOnly(new[] { "管管奇波，救救植物", "被阻截的路线" }),
            [3] = Array.AsReadOnly(new[] { "迷幻之舞", "按照书上所说", "猪王争霸", "好机会", "我盯上它了" }),
            [4] = Array.AsReadOnly(new[] { "新人的第一次", "成为好朋友吧", "吓唬一下就好", "抓住机会", "听说有甜的" }),
            [5] = Array.AsReadOnly(new[] { "像样的猎物", "前段勘路", "……能不能留几只", "影响训练", "谁准你们乱跑", "机会难得", "太多光点", "本来想带点藤蔓回去", "我就是笑了它一下", "安静处理" }),
            [6] = Array.AsReadOnly(new[] { "拿蝶练手", "太烫了，但我想吃", "……本来想捡海草", "别怪我没拦着", "暗焰兽太坏了", "锤子啊！！！", "卷土重来", "别惊扰太多", "外围清扫", "顺便采点草" })
        };
    private static readonly IReadOnlyList<string> TierSixSpecialChoices = Array.AsReadOnly(new[] { "已确认目标", "拿它们热身" });

    public static IReadOnlyList<string> GetCrisisChoices(int difficulty)
        => CrisisByDifficulty.GetValueOrDefault(difficulty) ?? Array.Empty<string>();
    public static IReadOnlyList<string> GetSpecialChoices(int difficulty)
        => difficulty == 6 ? TierSixSpecialChoices : Array.Empty<string>();

    public void Validate()
    {
        if (SchemaVersion != 3) throw new InvalidDataException("委托设置版本不受支持。");
        var dungeon = DailyChoices.SingleOrDefault(d => d.Name == DailyDungeon);
        if (dungeon is null || DailyDifficulty < dungeon.MinDifficulty || DailyDifficulty > dungeon.MaxDifficulty)
            throw new InvalidDataException("日常委托副本或难度无效。");
        if (!WeeklyChoices.Contains(WeeklyDungeon) || WeeklyDifficulty is < 1 or > 6 || CrisisDifficulty is < 1 or > 6)
            throw new InvalidDataException("往厄残影或危机讨伐难度无效。");
        foreach (var value in new[] { TeamName, StoryTeamName })
            if (value is null || value.Length > 80 || value.Any(char.IsControl)) throw new InvalidDataException("队伍或副本名称无效。");
        if (!GetStoryChoices(StoryChapter).Contains(StoryDungeon))
            throw new InvalidDataException("请选择当前主线章节下的关卡，原关卡不会自动替换。");
        if (CrisisNormalEnabled && !GetCrisisChoices(CrisisDifficulty).Contains(CrisisNormal))
            throw new InvalidDataException("请选择当前危机讨伐难度下的普通副本，原副本不会自动替换。");
        if (CrisisSpecialEnabled && (string.IsNullOrWhiteSpace(CrisisSpecial) || CrisisSpecial.Length > 80 || CrisisSpecial.Any(char.IsControl)))
            throw new InvalidDataException("已开启惊喜副本，请填写有效的完整副本名称（最多 80 字）。");
        ValidateClaimLimit(DailyClaimLimitEnabled, DailyClaimLimit, "日常委托");
        ValidateClaimLimit(WeeklyClaimLimitEnabled, WeeklyClaimLimit, "往厄残影");
        ValidateClaimLimit(CrisisSpecialEnabled && CrisisSpecialClaimLimitEnabled, CrisisSpecialClaimLimit, "危机惊喜副本");
        ValidateClaimLimit(CrisisNormalEnabled && CrisisNormalClaimLimitEnabled, CrisisNormalClaimLimit, "危机普通副本");
        ValidateClaimLimit(StoryClaimLimitEnabled, StoryClaimLimit, "主线委托");
    }

    private static void ValidateClaimLimit(bool enabled, int count, string name)
    {
        if (enabled && count is < 1 or > 999)
            throw new InvalidDataException($"{name}领取次数应为 1 至 999 的整数，按领取份数计算。");
    }
}

public sealed class CommissionSettingsStore
{
    private readonly string _path;
    public CommissionOptions Current { get; private set; } = new();
    public string? Error { get; private set; }
    public CommissionSettingsStore(string path)
    {
        _path = Path.GetFullPath(path);
        try
        {
            if (!File.Exists(_path)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var loaded = document.RootElement.Deserialize<CommissionOptions>() ?? throw new InvalidDataException("委托设置为空。");
            // Schema 1 originally always tried its saved special name before normal raids.
            // Its original default also allowed files without an explicit version field.
            var sourceSchema = document.RootElement.TryGetProperty(nameof(CommissionOptions.SchemaVersion), out _) ? loaded.SchemaVersion : 1;
            if (sourceSchema == 1)
                loaded = loaded with
                {
                    SchemaVersion = 3,
                    CrisisSpecialEnabled = document.RootElement.TryGetProperty(nameof(CommissionOptions.CrisisSpecialEnabled), out _)
                        ? loaded.CrisisSpecialEnabled : !string.IsNullOrWhiteSpace(loaded.CrisisSpecial)
                };
            else if (sourceSchema == 2) loaded = loaded with { SchemaVersion = 3 };
            Current = loaded;
            loaded.Validate();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        { Error = "委托设置读取失败：" + ex.Message; }
    }
    public bool Save(CommissionOptions options)
    {
        try { options.Validate(); SaveJson(_path, options); Current = options; Error = null; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { Error = "委托设置保存失败：" + ex.Message; return false; }
    }
    internal static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
