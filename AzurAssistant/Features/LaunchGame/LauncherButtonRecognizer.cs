using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Features.LaunchGame;

public sealed class LauncherButtonRecognizer(PpOcrReader reader)
{
    public async Task<ClientPoint?> FindAsync(FrameSnapshot frame, CancellationToken token)
    {
        var area = new PixelRegion(frame.Width * 68 / 100, frame.Height * 80 / 100,
            frame.Width * 24 / 100, frame.Height * 15 / 100);
        var lines = await reader.ReadAsync(frame, token, area, frame.Width < 1600 ? 2 : 1);
        var matches = lines.Where(line => line.Confidence >= 0.9
            && string.Concat(line.Text.Where(c => !char.IsWhiteSpace(c))) == "启动游戏").ToArray();
        if (matches.Length != 1) return null;
        var label = matches[0];
        var center = new ClientPoint((int)Math.Round(label.X + label.Width / 2), (int)Math.Round(label.Y + label.Height / 2));
        // 蓝色按钮在文字左右均留有内边距，可排除灰色禁用态及弹窗正文。
        return BlueAt((int)label.X - 4) && BlueAt((int)(label.X + label.Width) + 4) ? center : null;

        bool BlueAt(int x)
        {
            if ((uint)x >= frame.Width || (uint)center.Y >= frame.Height) return false;
            var pixel = frame.Pixels.Span.Slice(center.Y * frame.Stride + x * 4, 3);
            return pixel[0] >= 160 && pixel[1] >= 95 && pixel[0] - pixel[2] >= 80;
        }
    }
}
