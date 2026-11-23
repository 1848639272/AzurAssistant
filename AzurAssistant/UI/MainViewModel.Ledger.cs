using System.Collections.ObjectModel;
using System.IO;
using AzurAssistant.Contracts;
using AzurAssistant.Runtime;
using AzurAssistant.Vision;

namespace AzurAssistant.UI;

/// <summary>结算 OCR 记账：识别结算画面上的文字（或粘贴文本），累计收支。</summary>
public sealed partial class MainViewModel
{
    private LedgerStore? _ledgerStore;
    private PpOcrReader? _ledgerOcr;
    private string _ledgerText = "";
    private string _ledgerStatus = "点「识别当前画面」读取结算界面，或把结算文字粘贴到下面再点「按文本记账」。";
    private string _ledgerSummary = "还没有记账。";

    private string LedgerPath => Path.Combine(
        Path.GetDirectoryName(SoundsDirectory) ?? AppContext.BaseDirectory, "ledger.jsonl");

    private LedgerStore LedgerFile => _ledgerStore ??= new LedgerStore(LedgerPath);

    public ObservableCollection<LedgerEntry> LedgerEntries { get; } = [];

    public string LedgerText { get => _ledgerText; set { if (value == _ledgerText) return; _ledgerText = value; Notify(); } }

    public string LedgerStatus { get => _ledgerStatus; private set { _ledgerStatus = value; Notify(); } }

    public string LedgerSummary { get => _ledgerSummary; private set { _ledgerSummary = value; Notify(); } }

    /// <summary>Loads the saved ledger and refreshes the summary; called when the page opens.</summary>
    public void RefreshLedger()
    {
        LedgerEntries.Clear();
        var entries = LedgerFile.Load(300);
        foreach (var entry in entries.AsEnumerable().Reverse()) LedgerEntries.Add(entry);
        UpdateLedgerSummary(entries);
    }

    /// <summary>Books whatever the pasted text contains.</summary>
    public void BookLedgerText()
    {
        var lines = LedgerText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var entries = LedgerParser.Parse(lines);
        if (entries.Count == 0)
        {
            LedgerStatus = "这段文字里没有认出「物品 ×数量」这样的行。";
            return;
        }
        Book(entries, "文本记账");
    }

    /// <summary>Reads the current preview frame with OCR and books the recognised lines.</summary>
    public async Task BookLedgerFromScreenAsync()
    {
        if (!preview.IsRunning)
        {
            LedgerStatus = "请先在首页「连接」游戏画面，再点识别。";
            return;
        }
        try
        {
            LedgerStatus = "正在识别结算画面…";
            var frame = await preview.ReadAfterAsync(TimeSpan.Zero, CancellationToken.None);
            _ledgerOcr ??= new PpOcrReader();
            var regions = await _ledgerOcr.ReadAsync(frame, CancellationToken.None);
            var lines = regions.Select(region => region.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
            var entries = LedgerParser.Parse(lines);
            if (entries.Count == 0)
            {
                LedgerStatus = $"识别到 {lines.Count} 段文字，但没有找到「物品 ×数量」的行。可以把文字复制到下面用文本记账。";
                LedgerText = string.Join(Environment.NewLine, lines);
                return;
            }
            Book(entries, "画面识别");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
        {
            LedgerStatus = "识别失败：" + ex.Message;
        }
    }

    private void Book(IReadOnlyList<LedgerEntry> entries, string source)
    {
        if (!LedgerFile.Append(entries))
        {
            LedgerStatus = "写入 ledger.jsonl 失败（文件被占用？）。";
            return;
        }
        foreach (var entry in entries.AsEnumerable().Reverse()) LedgerEntries.Insert(0, entry);
        LedgerStatus = $"{source}：记了 {entries.Count} 条（{string.Join("、", entries.Take(3).Select(entry => entry.ToString()))}"
            + (entries.Count > 3 ? " …" : "") + "）。";
        Report("结算记账：" + LedgerStatus);
        UpdateLedgerSummary(LedgerFile.Load(1000));
    }

    private void UpdateLedgerSummary(IReadOnlyList<LedgerEntry> entries)
    {
        if (entries.Count == 0)
        {
            LedgerSummary = "还没有记账。";
            return;
        }
        var today = LedgerStore.DailyTotals(entries, DateTimeOffset.Now);
        var all = LedgerStore.Totals(entries);
        var todayText = today.Count == 0
            ? "今天还没有记录"
            : "今日：" + string.Join("、", today.Take(6).Select(pair => $"{pair.Name} {(pair.Net > 0 ? "+" : "")}{pair.Net:N0}"));
        var allText = "累计 " + entries.Count + " 条：" +
            string.Join("、", all.Take(8).Select(pair => $"{pair.Name} {(pair.Net > 0 ? "+" : "")}{pair.Net:N0}"));
        LedgerSummary = todayText + "\n" + allText;
    }

    public void ClearLedger()
    {
        LedgerFile.Clear();
        LedgerEntries.Clear();
        LedgerSummary = "还没有记账。";
        LedgerStatus = "已清空记账记录。";
    }

    public bool ExportLedger(string csvPath)
    {
        try
        {
            var entries = LedgerFile.Load(100000);
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("时间,物品,类型,数量,备注");
            foreach (var entry in entries)
                builder.AppendLine(string.Join(',', Quote(entry.BookedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                    Quote(entry.ItemName), Quote(entry.KindText), entry.Quantity.ToString(), Quote(entry.Note)));
            builder.AppendLine();
            builder.AppendLine("汇总,物品,净数量");
            foreach (var (name, net) in LedgerStore.Totals(entries))
                builder.AppendLine($"汇总,{Quote(name)},{net}");
            File.WriteAllText(csvPath, builder.ToString(), new System.Text.UTF8Encoding(true));
            LedgerStatus = "已导出：" + csvPath;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LedgerStatus = "导出失败：" + ex.Message;
            return false;
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
