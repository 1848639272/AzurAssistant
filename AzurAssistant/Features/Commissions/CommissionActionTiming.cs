namespace AzurAssistant.Features.Commissions;

/// <summary>Readiness buffers and the observation interval used only while automatic battle is confirmed.</summary>
public sealed record CommissionActionTiming(TimeSpan AutoBattleDelay, TimeSpan ResultDelay, TimeSpan MvpDelay)
{
    public TimeSpan AutoBattlePollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public static CommissionActionTiming Default { get; } = new(
        TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2));
}
