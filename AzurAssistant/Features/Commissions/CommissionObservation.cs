using AzurAssistant.Contracts;

namespace AzurAssistant.Features.Commissions;

public enum CommissionPage { Unknown, World, Menu, Hub, Daily, Weekly, Crisis, LeaveParty, Prepare, Teams, Lobby, Battle, Reward, Multiplier, Mvp, Result, CrisisResult, Story, BondLevelUp }
public enum CommissionRead { Details, Choices, Navigation }
public enum CommissionHubSection { Unknown, Regional, Assembly }
public enum RewardResource { Unknown, Stamina, BlueKey, YellowKey }
public sealed record CommissionObservation(CommissionPage Page, IReadOnlyDictionary<string, ClientPoint> Targets)
{
    public string Name { get; init; } = "";
    public string Team { get; init; } = "";
    public int? Difficulty { get; init; }
    public int? Chapter { get; init; }
    public int? Stamina { get; init; }
    public int? WeeklyRemaining { get; init; }
    public int? BlueKeys { get; init; }
    public int? YellowKeys { get; init; }
    public int? Cost { get; init; }
    public int? Multiplier { get; init; }
    public RewardResource Resource { get; init; }
    public bool AutoRunning { get; init; }
    public CommissionHubSection HubSection { get; init; }
    public string Diagnostic { get; init; } = "";
    public bool Has(string id) => Targets.ContainsKey(id);
    public string EvidenceKey => $"{Page}|{Name}|{Team}|{Difficulty}|{Stamina}|{WeeklyRemaining}|{BlueKeys}|{YellowKeys}|{Cost}|{Multiplier}|{Resource}|{AutoRunning}|{HubSection}|{Chapter}";
}
public interface ICommissionRecognizer
{
    Task<CommissionObservation> ObserveAsync(FrameSnapshot frame, CommissionRead read, CancellationToken token);
}

public static class CommissionResourcePlan
{
    public static int Multiplier(int available, int unitCost, bool weekly = false)
    {
        if (available < 0 || unitCost <= 0) throw new ArgumentOutOfRangeException(nameof(unitCost));
        return Math.Min(weekly ? 1 : 3, available / unitCost);
    }
}
