using System.IO;
using System.Windows.Threading;
using AzurAssistant.Platform;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>司凤小游戏：花瓣音游与祖赞卡之梯的识别 + 自动按键（参数全部可调，独立实现）。</summary>
public sealed partial class MainViewModel
{
    private DispatcherTimer? _minigameTimer;
    private RhythmDetector? _rhythmDetector;
    private JengaDetector? _jengaDetector;
    private readonly Dictionary<int, DateTimeOffset> _laneCooldown = new();
    private DateTimeOffset _ladderLastTap;
    private string _minigameStatus = "先连接游戏画面，再打开下面的自动开关。识别只读画面；按键只在游戏处于前台时发送。";
    private string _minigameRhythmText = "还没有分析过。";
    private string _minigameLadderText = "还没有分析过。";

    public MinigameConfig Minigame => MinigameConfig.Parse(Settings.MinigameConfigText);

    public bool MinigameRhythmAuto
    {
        get => Settings.MinigameRhythmAuto;
        set
        {
            if (value == Settings.MinigameRhythmAuto) return;
            if (SaveSettings(Settings with { MinigameRhythmAuto = value })) { Notify(); UpdateMinigameTimer(); }
        }
    }

    public bool MinigameLadderAuto
    {
        get => Settings.MinigameLadderAuto;
        set
        {
            if (value == Settings.MinigameLadderAuto) return;
            if (SaveSettings(Settings with { MinigameLadderAuto = value })) { Notify(); UpdateMinigameTimer(); }
        }
    }

    /// <summary>Uses the ported reference detector (AGPL, see reference/ok-azurpromilia) instead of the simple bands.</summary>
    public bool MinigameReferenceRhythm
    {
        get => Settings.MinigameReferenceRhythm;
        set
        {
            if (value == Settings.MinigameReferenceRhythm) return;
            if (SaveSettings(Settings with { MinigameReferenceRhythm = value })) { Notify(); }
        }
    }

    /// <summary>Uses the ported jenga detector (HUD gate + bird + template matching) for the hanging piece.</summary>
    public bool MinigameReferenceLadder
    {
        get => Settings.MinigameReferenceLadder;
        set
        {
            if (value == Settings.MinigameReferenceLadder) return;
            if (SaveSettings(Settings with { MinigameReferenceLadder = value })) Notify();
        }
    }

    /// <summary>How close (reference px) the note head must be to the judge line before pressing.</summary>
    public int MinigameTriggerWindow
    {
        get => Settings.MinigameTriggerWindow;
        set
        {
            var clamped = Math.Clamp(value, 20, 600);
            if (clamped == Settings.MinigameTriggerWindow) return;
            if (SaveSettings(Settings with { MinigameTriggerWindow = clamped })) Notify();
        }
    }

    public string MinigameStatus { get => _minigameStatus; private set { _minigameStatus = value; Notify(); } }
    public string MinigameRhythmText { get => _minigameRhythmText; private set { _minigameRhythmText = value; Notify(); } }
    public string MinigameLadderText { get => _minigameLadderText; private set { _minigameLadderText = value; Notify(); } }

    // ---- 可调参数（写回紧凑配置串） ----

    public double MinigameJudgeY { get => Minigame.JudgeY; set => SaveMinigame(Minigame with { JudgeY = value }); }
    public double MinigameBandHeight { get => Minigame.BandHeight; set => SaveMinigame(Minigame with { BandHeight = value }); }
    public int MinigameLaneCount { get => Minigame.LaneCount; set => SaveMinigame(Minigame with { LaneCount = Math.Clamp(value, 1, 12) }); }
    public double MinigameLaneLeft { get => Minigame.LaneLeft; set => SaveMinigame(Minigame with { LaneLeft = value }); }
    public double MinigameLaneRight { get => Minigame.LaneRight; set => SaveMinigame(Minigame with { LaneRight = value }); }
    public int MinigameMinPixels { get => Minigame.MinPixels; set => SaveMinigame(Minigame with { MinPixels = value }); }
    public int MinigameCooldownMs { get => Minigame.CooldownMs; set => SaveMinigame(Minigame with { CooldownMs = value }); }
    public double MinigameLadderTargetX { get => Minigame.LadderTargetX; set => SaveMinigame(Minigame with { LadderTargetX = value }); }
    public double MinigameLadderTolerance { get => Minigame.LadderTolerance; set => SaveMinigame(Minigame with { LadderTolerance = value }); }
    public double MinigameLadderTop { get => Minigame.LadderTop; set => SaveMinigame(Minigame with { LadderTop = value }); }
    public double MinigameLadderTopHeight { get => Minigame.LadderTopHeight; set => SaveMinigame(Minigame with { LadderTopHeight = value }); }
    public double MinigameLadderTowerY { get => Minigame.LadderTowerY; set => SaveMinigame(Minigame with { LadderTowerY = value }); }

    /// <summary>Virtual key for red notes (default E = 69).</summary>
    public string MinigameKeyRed
    {
        get => Minigame.KeyRed.ToString();
        set { if (TryVk(value, out var vk)) SaveMinigame(Minigame with { KeyRed = vk }); }
    }

    /// <summary>Virtual key for blue notes (default Q = 81).</summary>
    public string MinigameKeyBlue
    {
        get => Minigame.KeyBlue.ToString();
        set { if (TryVk(value, out var vk)) SaveMinigame(Minigame with { KeyBlue = vk }); }
    }

    /// <summary>Purple notes need two keys at once; shown as "69+81".</summary>
    public string MinigameKeyPurple
    {
        get => $"{Minigame.KeyPurpleA}+{Minigame.KeyPurpleB}";
        set
        {
            var parts = value.Split(['+', ',', '，', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !TryVk(parts[0], out var first)) return;
            var second = parts.Length > 1 && TryVk(parts[1], out var parsed) ? parsed : first;
            SaveMinigame(Minigame with { KeyPurpleA = first, KeyPurpleB = second });
        }
    }

    /// <summary>Key pressed to release the hanging piece (default space = 32).</summary>
    public string MinigameKeyLadder
    {
        get => Minigame.LadderKey.ToString();
        set { if (TryVk(value, out var vk)) SaveMinigame(Minigame with { LadderKey = vk }); }
    }

    private static bool TryVk(string? text, out int virtualKey)
        => int.TryParse(text?.Trim(), out virtualKey) && virtualKey is > 0 and <= 0xFF;


    private void SaveMinigame(MinigameConfig config)
    {
        if (!SaveSettings(Settings with { MinigameConfigText = config.Serialize() })) return;
        Notify(nameof(MinigameJudgeY));
        Notify(nameof(MinigameBandHeight));
        Notify(nameof(MinigameLaneCount));
        Notify(nameof(MinigameLaneLeft));
        Notify(nameof(MinigameLaneRight));
        Notify(nameof(MinigameMinPixels));
        Notify(nameof(MinigameCooldownMs));
        Notify(nameof(MinigameKeyRed));
        Notify(nameof(MinigameKeyBlue));
        Notify(nameof(MinigameKeyPurple));
        Notify(nameof(MinigameKeyLadder));
        Notify(nameof(MinigameLadderTargetX));
        Notify(nameof(MinigameLadderTolerance));
        Notify(nameof(MinigameLadderTop));
        Notify(nameof(MinigameLadderTopHeight));
        Notify(nameof(MinigameLadderTowerY));
    }

    /// <summary>Turns the polling loop on only while at least one helper is enabled.</summary>
    private void UpdateMinigameTimer()
    {
        var wanted = MinigameRhythmAuto || MinigameLadderAuto;
        if (!wanted) { _minigameTimer?.Stop(); return; }
        _minigameTimer ??= CreateMinigameTimer();
        if (!_minigameTimer.IsEnabled) _minigameTimer.Start();
    }

    private DispatcherTimer CreateMinigameTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => MinigameTick();
        return timer;
    }

    /// <summary>One polling pass: read the latest frame, analyse, and (if enabled) send the key.</summary>
    public void MinigameTick()
    {
        var snapshot = preview.Current;
        var frame = snapshot.Frame;
        if (frame is null || snapshot.State != PreviewState.Live)
        {
            MinigameStatus = "没有画面：先在首页「连接」游戏画面。";
            return;
        }
        var config = Minigame;
        var pixels = frame.Pixels.Span;
        var now = DateTimeOffset.Now;

        if (MinigameRhythmAuto && MinigameReferenceRhythm)
        {
            _rhythmDetector ??= new RhythmDetector();
            var bytes = frame.Pixels.ToArray();
            if (!_rhythmDetector.IsActive(bytes, frame.Width, frame.Height))
            {
                MinigameRhythmText = "参考识别：没找到右上角的颜色图例，先把游戏切到音游界面。";
            }
            else
            {
                var notes = _rhythmDetector.Detect(bytes, frame.Width, frame.Height);
                MinigameRhythmText = RhythmDetector.Describe(notes);
                foreach (var note in notes)
                {
                    if (Math.Abs(note.Head - RhythmDetector.JudgeX) > MinigameTriggerWindow) continue;
                    var lane = Math.Clamp((int)Math.Round(note.Head), 0, 7);
                    if (_laneCooldown.TryGetValue(lane, out var last) && now - last < TimeSpan.FromMilliseconds(config.CooldownMs)) continue;
                    if (!SyntheticInput.IsForegroundOf(frame.ProcessId)) { MinigameStatus = "游戏不在前台，按键已暂停。"; break; }
                    var combo = config.KeysFor(note.Color);
                    if (combo.Length > 0) SyntheticInput.TapCombo(combo.Select(key => (ushort)key).ToArray());
                    _laneCooldown[lane] = now;
                }
            }
        }
        else if (MinigameRhythmAuto)
        {
            var hits = RhythmAnalyzer.Detect(pixels, frame.Width, frame.Height, config);
            MinigameRhythmText = RhythmAnalyzer.Describe(hits);
            foreach (var hit in hits)
            {
                var combo = config.KeysFor(hit.Color);
                if (combo.Length == 0) continue;
                if (_laneCooldown.TryGetValue(hit.Lane, out var last) && now - last < TimeSpan.FromMilliseconds(config.CooldownMs)) continue;
                if (!SyntheticInput.IsForegroundOf(frame.ProcessId))
                {
                    MinigameStatus = "游戏不在前台，按键已暂停。";
                    break;
                }
                SyntheticInput.TapCombo(combo.Select(key => (ushort)key).ToArray());
                _laneCooldown[hit.Lane] = now;
            }
        }

        if (MinigameLadderAuto && MinigameReferenceLadder)
        {
            _jengaDetector ??= new JengaDetector();
            var bytes = frame.Pixels.ToArray();
            using var normalized = JengaDetector.Normalize(bytes, frame.Width, frame.Height);
            if (normalized is null)
            {
                MinigameLadderText = "参考识别：画面不是 16:9，无法归一化。";
            }
            else
            {
                var observation = _jengaDetector.Detect(normalized, config.LadderTolerance);
                MinigameLadderText = JengaDetector.Describe(observation);
                var aligned = observation.Carried is not null && !double.IsNaN(observation.TowerX)
                    && Math.Abs(observation.Carried.X - observation.TowerX) <= config.LadderTolerance;
                if (aligned && now - _ladderLastTap > TimeSpan.FromMilliseconds(Math.Max(200, config.CooldownMs)))
                {
                    if (SyntheticInput.IsForegroundOf(frame.ProcessId))
                    {
                        SyntheticInput.Tap((ushort)config.LadderKey);
                        _ladderLastTap = now;
                        MinigameStatus = "参考识别：悬挂件与承接面对齐，已松手。";
                    }
                    else MinigameStatus = "游戏不在前台，按键已暂停。";
                }
            }
        }
        else if (MinigameLadderAuto)
        {
            var observation = LadderAnalyzer.Detect(pixels, frame.Width, frame.Height, config);
            MinigameLadderText = LadderAnalyzer.Describe(observation);
            if (observation.Aligned && now - _ladderLastTap > TimeSpan.FromMilliseconds(Math.Max(200, config.CooldownMs)))
            {
                if (SyntheticInput.IsForegroundOf(frame.ProcessId))
                {
                    SyntheticInput.Tap((ushort)config.LadderKey);
                    _ladderLastTap = now;
                    MinigameStatus = "已松手：悬挂件与承接面对齐。";
                }
                else MinigameStatus = "游戏不在前台，按键已暂停。";
            }
        }
    }

    /// <summary>Analyses the current frame once without sending any input (used by 先看一眼).</summary>
    public void AnalyzeMinigameOnce()
    {
        var snapshot = preview.Current;
        var frame = snapshot.Frame;
        if (frame is null)
        {
            MinigameStatus = "没有画面：先在首页「连接」游戏画面。";
            return;
        }
        var config = Minigame;
        var pixels = frame.Pixels.Span;
        MinigameRhythmText = RhythmAnalyzer.Describe(RhythmAnalyzer.Detect(pixels, frame.Width, frame.Height, config));
        MinigameLadderText = LadderAnalyzer.Describe(LadderAnalyzer.Detect(pixels, frame.Width, frame.Height, config));
        MinigameStatus = $"已分析一帧（{frame.Width}×{frame.Height}），没有发送任何按键。";
    }

    /// <summary>Uses the current frame's ladder position as the target column (rough one-click calibration).</summary>
    public void CalibrateLadderTarget()
    {
        var frame = preview.Current.Frame;
        if (frame is null) { MinigameStatus = "没有画面，无法校准。"; return; }
        var observation = LadderAnalyzer.Detect(frame.Pixels.Span, frame.Width, frame.Height, Minigame);
        if (double.IsNaN(observation.TowerX)) { MinigameStatus = "校准失败：没在承接面区域找到目标。"; return; }
        MinigameLadderTargetX = Math.Round(observation.TowerX);
        MinigameStatus = $"目标列已设为 x={Minigame.LadderTargetX:0}。";
    }

    public void StopMinigame()
    {
        MinigameRhythmAuto = false;
        MinigameLadderAuto = false;
        _minigameTimer?.Stop();
        MinigameStatus = "已停止小游戏辅助。";
    }

    public void PrepareMinigamePage()
    {
        UpdateMinigameTimer();
    }
}
