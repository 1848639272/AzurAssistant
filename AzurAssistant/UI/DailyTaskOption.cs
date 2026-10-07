using System.ComponentModel;
using AzurAssistant.Composition;

namespace AzurAssistant.UI;

public sealed class DailyTaskOption(DailyFeatureChoice choice, MainViewModel owner) : INotifyPropertyChanged
{
    private bool _isExpanded;
    public string Id => choice.Id;
    public string Name => choice.Name;
    public MainViewModel Owner => owner;
    public bool HasSettings => Id is "daily-commissions" or "weekly-echoes" or "crisis-raids" or "story-commissions";
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; PropertyChanged?.Invoke(this, new(string.Empty)); }
    }
    public bool IsSelected { get => owner.IsDailySelected(Id); set { owner.SetDailySelected(Id, value); Refresh(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
}
