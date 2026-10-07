using System.IO;
using System.Net.Http;
using System.Windows;
using AzurAssistant.Platform;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

public partial class MainWindow
{
    private AssistantUpdate? _updates;
    private GitHubReleaseClient? _releaseClient;
    private CancellationTokenSource? _updateCancellation;
    private bool _updateLaunched;

    internal void AttachUpdates(AssistantUpdate updates, GitHubReleaseClient client, HttpClient http, string status)
    {
        _updates = updates;
        _releaseClient = client;
        ViewModel.SetUpdateStatus(false, status);
        Closed += (_, _) => { _updateCancellation?.Cancel(); http.Dispose(); };
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(automatic: false);
    private void OnCancelUpdate(object sender, RoutedEventArgs e) => _updateCancellation?.Cancel();

    private async Task CheckUpdatesAsync(bool automatic)
    {
        if (!ViewModel.CanCheckUpdates || _closing) return;
        if (_updates is null)
        {
            if (!automatic) MessageBox.Show(this, ViewModel.UpdateStatus, "蔚蓝助手 · 检查更新");
            return;
        }
        if (automatic && !ViewModel.EffectiveAutoUpdate) return;
        using var cancellation = new CancellationTokenSource();
        _updateCancellation = cancellation;
        string? downloaded = null;
        ViewModel.SetUpdateStatus(true, "正在检查 GitHub 更新…");
        try
        {
            var result = await _updates.CheckAsync(automatic, ViewModel.EffectiveAutoUpdate, cancellation.Token);
            ViewModel.SetUpdateStatus(true, result.Message);
            if (_closing || cancellation.IsCancellationRequested) return;
            if (result.Update is not { } update)
            {
                if ((!automatic && result.Status != UpdateCheckStatus.Skipped) || result.Status == UpdateCheckStatus.Failed)
                    MessageBox.Show(this, result.Message, "蔚蓝助手 · 检查更新", MessageBoxButton.OK,
                        result.Status == UpdateCheckStatus.Failed ? MessageBoxImage.Warning : MessageBoxImage.Information);
                return;
            }
            var notes = update.Notes.Length > 2000 ? update.Notes[..2000] + "…" : update.Notes;
            if (MessageBox.Show(this, $"发现新版本：v{update.Release.ProductVersion}\n构建编号：{update.Release.BuildNumber}\n\n{notes}\n\n是否下载完整安装包并更新？安装前会停止当前任务，保留设置与路线。",
                "蔚蓝助手 · 发现更新", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzurAssistant", "updates", Guid.NewGuid().ToString("N"));
            var progress = new Progress<double>(percent => { if (!_closing) ViewModel.SetUpdateStatus(true, $"正在下载完整安装包：{percent:0}%"); });
            downloaded = await _releaseClient!.DownloadAsync(update, directory, progress, cancellation.Token);
            if (_closing || cancellation.IsCancellationRequested) return;
            ViewModel.SetUpdateStatus(true, "安装包校验通过，正在停止任务…");
            _updateLaunched = await ViewModel.InstallUpdateAsync(() => !cancellation.IsCancellationRequested &&
                AssistantInstaller.TryStart(downloaded, AppContext.BaseDirectory, message => ViewModel.SetUpdateStatus(true, message)));
            if (_updateLaunched) Close();
            else MessageBox.Show(this, "安装程序未启动，当前窗口已保留。请重试或手动运行安装包。", "蔚蓝助手 · 更新失败");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { ViewModel.SetUpdateStatus(true, "已取消检查或下载更新。"); }
        catch (Exception ex)
        {
            var message = ex is HttpRequestException or OperationCanceledException
                ? "连接 GitHub 失败，请检查网络后重试。" : $"更新失败：{ex.Message}";
            ViewModel.SetUpdateStatus(true, message);
            if (!_closing) MessageBox.Show(this, message, "蔚蓝助手 · 更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateCancellation = null;
            ViewModel.SetUpdateStatus(false, ViewModel.UpdateStatus);
            if (!_updateLaunched && downloaded is not null)
            {
                try { File.Delete(downloaded); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
