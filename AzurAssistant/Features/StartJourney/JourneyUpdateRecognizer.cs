using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Features.StartJourney;

public sealed class JourneyUpdateRecognizer(TemplateCatalog templates)
{
    public JourneyObservation? Observe(FrameSnapshot frame)
    {
        var title = templates.Match(frame, "update-title");
        var required = templates.Match(frame, "update-required");
        var resources = templates.Match(frame, "update-resources");
        var confirm = templates.Match(frame, "update-confirm");
        var restart = templates.Match(frame, "update-restart");
        if (title is not null && confirm is { } button)
        {
            if (restart is not null && required is null && resources is null)
                return new(JourneyPage.RestartPrompt, ConfirmPoint: button.Center);
            if (restart is null && required is { } prefix && resources is { } suffix
                && suffix.Center.X > prefix.Center.X && Math.Abs(prefix.Center.Y - suffix.Center.Y) <= 6)
                return new(JourneyPage.UpdatePrompt, ConfirmPoint: button.Center);
        }
        // 弹窗证据不完整时阻止使用背景页面的点击目标。
        if (title is not null || required is not null || resources is not null || confirm is not null || restart is not null)
            return new(JourneyPage.Unknown);
        return templates.Match(frame, "update-progress") is not null ? new(JourneyPage.Updating) : null;
    }
}
