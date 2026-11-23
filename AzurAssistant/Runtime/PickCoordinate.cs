using System.Windows;

namespace AzurAssistant.Runtime;

/// <summary>Converts a screen point into game-client and 1920×1080 reference coordinates.</summary>
public static class PickCoordinate
{
    public const int ReferenceWidth = 1920;
    public const int ReferenceHeight = 1080;

    public sealed record Result(int ClientX, int ClientY, int ReferenceX, int ReferenceY, bool InsideClient)
    {
        public string Describe(int clientWidth, int clientHeight) => InsideClient
            ? $"客户区 ({ClientX}, {ClientY})　参考 ({ReferenceX}, {ReferenceY})　客户区尺寸 {clientWidth}×{clientHeight}"
            : $"屏幕点 ({ClientX}, {ClientY})：不在游戏客户区内（客户区 {clientWidth}×{clientHeight}）";

        /// <summary>Ready-to-paste snippet for flow files.</summary>
        public string Snippet => $"\"x\": {ReferenceX}, \"y\": {ReferenceY}";
    }

    public static Result FromScreen(int screenX, int screenY, int clientLeft, int clientTop, int clientWidth, int clientHeight)
    {
        var relativeX = screenX - clientLeft;
        var relativeY = screenY - clientTop;
        var inside = clientWidth > 0 && clientHeight > 0
            && relativeX >= 0 && relativeY >= 0 && relativeX < clientWidth && relativeY < clientHeight;
        var referenceX = clientWidth > 0 ? (int)Math.Round(relativeX * (double)ReferenceWidth / clientWidth) : relativeX;
        var referenceY = clientHeight > 0 ? (int)Math.Round(relativeY * (double)ReferenceHeight / clientHeight) : relativeY;
        return new Result(relativeX, relativeY, referenceX, referenceY, inside);
    }
}
