using System.Globalization;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>抽卡分析：按 GGanalysis 的保底+伯努利模型算期望、概率与最坏情况。</summary>
public sealed partial class MainViewModel
{
    public enum GachaPresetKind
    {
        Character,
        Weapon,
        Custom,
    }

    public sealed record GachaPreset(string Label, GachaPresetKind Kind);

    private GachaPresetKind _gachaPreset = GachaPresetKind.Character;
    private GachaPreset? _selectedGachaPreset;
    private string _gachaTableText = "";
    private double _gachaBasePercent = 0.6;
    private int _gachaHardPity = 90;
    private int _gachaSoftPityStart = 74;
    private double _gachaSoftStepPercent = 6;
    private double _gachaItemPercent = 50;
    private int _gachaCopies = 1;
    private int _gachaStartPity;
    private int _gachaPulls = 180;
    private string _gachaResult = "";
    private string _gachaTableTextCache = "";
    private string _gachaStatus = "按 GGanalysis 的 PityBernoulliModel 计算：每抽按当前保底计数查概率，出货后再按「是否为目标」判定。";

    public IReadOnlyList<GachaPreset> GachaPresets { get; } =
    [
        new("角色（0.6% / 90 保底 / 74 起软保底）", GachaPresetKind.Character),
        new("武器（1.6% / 80 保底）", GachaPresetKind.Weapon),
        new("自定义", GachaPresetKind.Custom),
    ];

    public GachaPreset SelectedGachaPreset
    {
        get => _selectedGachaPreset ??= GachaPresets[0];
        set
        {
            if (value is null || ReferenceEquals(_selectedGachaPreset, value)) return;
            _selectedGachaPreset = value;
            _gachaPreset = value.Kind;
            if (value.Kind == GachaPresetKind.Character)
            {
                _gachaBasePercent = 0.6; _gachaHardPity = 90; _gachaSoftPityStart = 74; _gachaSoftStepPercent = 6;
            }
            else if (value.Kind == GachaPresetKind.Weapon)
            {
                _gachaBasePercent = 1.6; _gachaHardPity = 80; _gachaSoftPityStart = 0; _gachaSoftStepPercent = 0;
            }
            NotifyAllGacha();
            RecomputeGacha();
        }
    }

    /// <summary>Optional explicit table ("0.006;0.006;…"); empty means "use the fields above".</summary>
    public string GachaTableText
    {
        get => _gachaTableText;
        set
        {
            if (value == _gachaTableText) return;
            _gachaTableText = value;
            Notify();
            RecomputeGacha();
        }
    }

    public double GachaBasePercent
    {
        get => _gachaBasePercent;
        set => SetGachaValue(ref _gachaBasePercent, Math.Clamp(value, 0.01, 100));
    }

    public int GachaHardPity
    {
        get => _gachaHardPity;
        set => SetGachaValue(ref _gachaHardPity, Math.Clamp(value, 2, 200));
    }

    public int GachaSoftPityStart
    {
        get => _gachaSoftPityStart;
        set => SetGachaValue(ref _gachaSoftPityStart, Math.Clamp(value, 0, 200));
    }

    public double GachaSoftStepPercent
    {
        get => _gachaSoftStepPercent;
        set => SetGachaValue(ref _gachaSoftStepPercent, Math.Clamp(value, 0, 100));
    }

    public double GachaItemPercent
    {
        get => _gachaItemPercent;
        set => SetGachaValue(ref _gachaItemPercent, Math.Clamp(value, 1, 100));
    }

    public int GachaCopies
    {
        get => _gachaCopies;
        set => SetGachaValue(ref _gachaCopies, Math.Clamp(value, 1, GachaModel.MaxCopies));
    }

    public int GachaStartPity
    {
        get => _gachaStartPity;
        set => SetGachaValue(ref _gachaStartPity, Math.Clamp(value, 0, 200));
    }

    public int GachaPulls
    {
        get => _gachaPulls;
        set => SetGachaValue(ref _gachaPulls, Math.Clamp(value, 1, GachaModel.MaxPulls));
    }

    public string GachaResult { get => _gachaResult; private set { _gachaResult = value; Notify(); } }

    public string GachaStatus { get => _gachaStatus; private set { _gachaStatus = value; Notify(); } }

    private void SetGachaValue<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Notify();
        RecomputeGacha();
    }

    private void NotifyAllGacha()
    {
        Notify(nameof(GachaBasePercent));
        Notify(nameof(GachaHardPity));
        Notify(nameof(GachaSoftPityStart));
        Notify(nameof(GachaSoftStepPercent));
        Notify(nameof(GachaItemPercent));
        Notify(nameof(GachaCopies));
        Notify(nameof(GachaStartPity));
        Notify(nameof(GachaPulls));
    }

    /// <summary>Builds the pity table from the fields (or the explicit table when one is supplied).</summary>
    private IReadOnlyList<double> BuildGachaTable()
    {
        var custom = GachaModel.ParseTable(_gachaTableText);
        if (custom.Count >= 2)
        {
            _gachaTableTextCache = $"自定义 {custom.Count - 1} 抽表";
            return custom;
        }
        var hard = Math.Clamp(_gachaHardPity, 2, 200);
        var table = new double[hard + 1];
        var baseRate = _gachaBasePercent / 100;
        var step = _gachaSoftStepPercent / 100;
        var softStart = _gachaSoftPityStart <= 0 || _gachaSoftPityStart >= hard ? hard : _gachaSoftPityStart;
        for (var pull = 1; pull <= hard; pull++)
        {
            if (pull >= hard) table[pull] = 1;
            else if (step > 0 && pull >= softStart) table[pull] = Math.Min(1, baseRate + (pull - softStart + 1) * step);
            else table[pull] = baseRate;
        }
        _gachaTableTextCache = $"{hard} 抽表（{_gachaBasePercent:0.##}% 起，{softStart} 起软保底 +{_gachaSoftStepPercent:0.##}%/抽）";
        return table;
    }

    public void RecomputeGacha()
    {
        try
        {
            var table = BuildGachaTable();
            var model = new GachaModel(table, _gachaItemPercent / 100);
            var maxPulls = Math.Min(GachaModel.MaxPulls, model.MaxPity * Math.Max(2, _gachaCopies) + 10);
            var expected = model.Expected(_gachaCopies, _gachaStartPity, maxPulls);
            var within = model.ProbabilityWithin(_gachaPulls, _gachaCopies, _gachaStartPity, maxPulls);
            var worst = model.WorstCase(_gachaCopies, _gachaStartPity);
            GachaResult =
                $"模型：{_gachaTableTextCache}，出目标概率 {_gachaItemPercent:0.##}%\n"
                + $"目标 {_gachaCopies} 个（当前已垫 {_gachaStartPity} 抽）："
                + $"期望 {expected:0.0} 抽，{_gachaPulls} 抽内完成概率 {within * 100:0.00}%，最坏 {worst} 抽";
            GachaStatus = $"已按 GGanalysis 的 PityBernoulliModel 计算（保底表 {table.Count - 1} 抽，目标概率 {model.ItemProbability:0.###}）。";
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            GachaStatus = "参数无效：" + ex.Message;
        }
    }

    /// <summary>Fills the page with the current numbers the first time it is opened.</summary>
    public void PrepareGachaPage()
    {
        if (_gachaResult.Length == 0) RecomputeGacha();
    }

    public static string GachaModelSource =>
        "模型来自 OneBST/GGanalysis 的 PityBernoulliModel（保底层 + 伯努利层）；这里用 C# 重写，未包含其 Python 代码。";
}
