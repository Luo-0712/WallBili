using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;

namespace WallBili.Desktop;

/// <summary>QR-code and cookie-import login dialog. Polls every two seconds for at most 180 seconds per
/// challenge; closing the dialog, requesting a new code or exiting the app cancels pending polls.</summary>
public partial class LoginDialog : Window
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(180);

    private readonly BilibiliClient _client;
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };
    private readonly CancellationTokenSource _polls = new();
    private QrLoginChallenge? _challenge;
    private DateTimeOffset _deadline;
    private bool _polling;
    private bool _closed;

    /// <summary>Set after any login method validated and applied the account; persisted by the owner.</summary>
    public string? CookieResult { get; private set; }

    public LoginDialog(BilibiliClient client)
    {
        _client = client;
        InitializeComponent();
        _timer.Tick += async (_, _) => await PollAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshQrAsync();

    private async void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_closed || !IsLoaded) return;
        if (Tabs.SelectedIndex == 0) await RefreshQrAsync();
        else StopPolling();
    }

    private async void RefreshQr_Click(object sender, RoutedEventArgs e) => await RefreshQrAsync();

    private async Task RefreshQrAsync()
    {
        StopPolling();
        QrStatus.Text = "正在获取二维码…";
        try
        {
            // Creating a new challenge supersedes the old key: late polls of it are inert.
            _challenge = await _client.CreateQrLoginAsync(_polls.Token);
            _deadline = DateTimeOffset.Now + MaximumWait;
            RenderQr(_challenge.LoginUrl);
            QrStatus.Text = "请使用 B 站手机客户端扫码";
            _timer.Start();
            await PollAsync();
        }
        catch (OperationCanceledException) when (_polls.IsCancellationRequested) { }
        catch (ContentAccessException ex)
        {
            QrStatus.Text = ex.Kind is ContentFailureKind.Network or ContentFailureKind.RateLimited
                ? "暂时无法获取二维码，请检查网络后点击重新获取。"
                : "获取二维码失败，请稍后点击重新获取。";
        }
    }

    private void RenderQr(Uri loginUrl)
    {
        // QRCoder renders locally; the login URL never leaves this machine through a third-party service.
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(loginUrl.AbsoluteUri, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(png.GetGraphic(12));
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        QrImage.Source = image;
    }

    private async Task PollAsync()
    {
        if (_closed || _polling || _challenge is not { } challenge || !_timer.IsEnabled) return;
        if (DateTimeOffset.Now >= _deadline)
        {
            StopPolling();
            QrStatus.Text = "二维码已过期，请点击“重新获取二维码”。";
            return;
        }
        _polling = true;
        try
        {
            var poll = await _client.PollQrLoginAsync(challenge.QrcodeKey, _polls.Token);
            if (_challenge.QrcodeKey != challenge.QrcodeKey) return; // A newer challenge exists; drop this result.
            switch (poll.State)
            {
                case QrLoginState.WaitingScan:
                    QrStatus.Text = "等待扫描…";
                    return;
                case QrLoginState.WaitingConfirm:
                    QrStatus.Text = "已扫码，请在手机上确认登录";
                    return;
                case QrLoginState.Expired:
                    StopPolling();
                    QrStatus.Text = "二维码已过期，请点击“重新获取二维码”。";
                    return;
                case QrLoginState.Superseded:
                    return;
                case QrLoginState.Succeeded:
                    StopPolling();
                    QrStatus.Text = $"登录成功：{BilibiliCookieText.Describe(poll.Account!)}";
                    CookieResult = poll.CookieText;
                    DialogResult = true;
                    return;
            }
        }
        catch (OperationCanceledException) when (_polls.IsCancellationRequested) { }
        catch (ContentAccessException ex)
        {
            StopPolling();
            QrStatus.Text = ex.Kind is ContentFailureKind.Network or ContentFailureKind.RateLimited
                ? "网络暂时不可用，请点击重新获取二维码重试。"
                : "登录状态查询失败，请点击重新获取二维码重试。";
        }
        finally { _polling = false; }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var text = CookieInput.Text.Trim();
        ImportStatus.Text = "正在验证导入的 Cookie…";
        try
        {
            var account = await _client.ImportCookiesAsync(text, _polls.Token);
            StopPolling();
            ImportStatus.Text = $"导入成功：{BilibiliCookieText.Describe(account)}";
            CookieResult = BilibiliCookieText.Normalize(text);
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_polls.IsCancellationRequested) { }
        catch (ContentAccessException ex)
        {
            ImportStatus.Text = ex.Kind switch
            {
                ContentFailureKind.InvalidInput => $"导入失败：{ex.Message}",
                ContentFailureKind.AuthenticationRequired => "导入失败：Cookie 无效或已过期，请重新获取。",
                ContentFailureKind.Network or ContentFailureKind.RateLimited => "导入失败：网络暂时不可用，请稍后重试。",
                _ => "导入失败：无法验证该 Cookie，请稍后重试。"
            };
        }
    }

    private void StopPolling()
    {
        _timer.Stop();
        _challenge = null;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Closing the dialog (or the whole app) cancels any pending poll before it can replace the session.
        _closed = true;
        StopPolling();
        _polls.Cancel();
        _polls.Dispose();
    }
}
