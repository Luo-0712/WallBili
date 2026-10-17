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
    private CancellationTokenSource? _qrRequest;
    private CancellationTokenSource? _importRequest;
    private CancellationTokenSource? _pollingRequest;
    private QrLoginChallenge? _challenge;
    private DateTimeOffset _deadline;
    private bool _loaded;
    private bool _closed;

    /// <summary>Set after any login method validated and applied the account; persisted by the owner.</summary>
    public string? CookieResult { get; private set; }

    public LoginDialog(BilibiliClient client)
    {
        _client = client;
        InitializeComponent();
        _timer.Tick += async (_, _) => await PollAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        if (Tabs.SelectedIndex == 0) await RefreshQrAsync();
    }

    private async void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_closed || !_loaded || e.Source != Tabs) return;
        if (Tabs.SelectedIndex == 0) await RefreshQrAsync();
        else StopPolling();
    }

    private async void RefreshQr_Click(object sender, RoutedEventArgs e) => await RefreshQrAsync();

    private async Task RefreshQrAsync()
    {
        if (_closed) return;
        CancelImport();
        StopPolling();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_polls.Token);
        _qrRequest = request;
        QrStatus.Text = "正在获取二维码…";
        try
        {
            var challenge = await _client.CreateQrLoginAsync(request.Token);
            if (!IsCurrentQrRequest(request)) return;
            _challenge = challenge;
            _deadline = DateTimeOffset.Now + MaximumWait;
            RenderQr(_challenge.LoginUrl);
            QrStatus.Text = "请使用 B 站手机客户端扫码";
            _timer.Start();
            await PollAsync();
        }
        catch (OperationCanceledException) { }
        catch (ContentAccessException ex)
        {
            if (!IsCurrentQrRequest(request)) return;
            StopPolling();
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
        using var stream = new MemoryStream(png.GetGraphic(12));
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        QrImage.Source = image;
    }

    private async Task PollAsync()
    {
        var request = _qrRequest;
        if (request is null || !IsCurrentQrRequest(request) || ReferenceEquals(_pollingRequest, request) ||
            _challenge is not { } challenge || !_timer.IsEnabled) return;
        if (DateTimeOffset.Now >= _deadline)
        {
            StopPolling();
            QrStatus.Text = "二维码已过期，请点击“重新获取二维码”。";
            return;
        }
        _pollingRequest = request;
        try
        {
            var poll = await _client.PollQrLoginAsync(challenge.QrcodeKey, request.Token);
            if (!IsCurrentQrRequest(request) || _challenge?.QrcodeKey != challenge.QrcodeKey) return;
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
                    StopPolling();
                    QrStatus.Text = "登录请求已失效，请重新获取二维码。";
                    return;
                case QrLoginState.Succeeded:
                    StopPolling();
                    QrStatus.Text = $"登录成功：{BilibiliCookieText.Describe(poll.Account!)}";
                    CookieResult = poll.CookieText;
                    DialogResult = true;
                    return;
            }
        }
        catch (OperationCanceledException) { }
        catch (ContentAccessException ex)
        {
            if (!IsCurrentQrRequest(request)) return;
            StopPolling();
            QrStatus.Text = ex.Kind is ContentFailureKind.Network or ContentFailureKind.RateLimited
                ? "网络暂时不可用，请点击重新获取二维码重试。"
                : "登录状态查询失败，请点击重新获取二维码重试。";
        }
        finally
        {
            // A late completion must not clear a newer generation's in-flight marker.
            if (ReferenceEquals(_pollingRequest, request)) _pollingRequest = null;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await ImportAsync();

    private async Task ImportAsync()
    {
        if (_closed) return;
        StopPolling();
        CancelImport();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_polls.Token);
        _importRequest = request;
        var text = CookieInput.Text.Trim();
        ImportStatus.Text = "正在验证导入的 Cookie…";
        try
        {
            var account = await _client.ImportCookiesAsync(text, request.Token);
            if (!IsCurrentImport(request)) return;
            ImportStatus.Text = $"导入成功：{BilibiliCookieText.Describe(account)}";
            CookieResult = BilibiliCookieText.Normalize(text);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentImport(request)) ImportStatus.Text = "登录请求已取消，请重试。";
        }
        catch (ContentAccessException ex)
        {
            if (!IsCurrentImport(request)) return;
            ImportStatus.Text = ex.Kind switch
            {
                ContentFailureKind.InvalidInput => $"导入失败：{ex.Message}",
                ContentFailureKind.AuthenticationRequired => "导入失败：Cookie 无效或已过期，请重新获取。",
                ContentFailureKind.Network or ContentFailureKind.RateLimited => "导入失败：网络暂时不可用，请稍后重试。",
                _ => "导入失败：无法验证该 Cookie，请稍后重试。"
            };
        }
        finally
        {
            if (ReferenceEquals(_importRequest, request))
            {
                _importRequest = null;
                request.Dispose();
            }
        }
    }

    private bool IsCurrentQrRequest(CancellationTokenSource request) =>
        !_closed && ReferenceEquals(_qrRequest, request) && !request.IsCancellationRequested;

    private bool IsCurrentImport(CancellationTokenSource request) =>
        !_closed && ReferenceEquals(_importRequest, request) && !request.IsCancellationRequested;

    private void StopPolling()
    {
        _timer.Stop();
        _challenge = null;
        var request = _qrRequest;
        _qrRequest = null;
        request?.Cancel();
        request?.Dispose();
        _client.CancelQrLogin();
    }

    private void CancelImport()
    {
        var request = _importRequest;
        _importRequest = null;
        request?.Cancel();
        request?.Dispose();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Closing the dialog (or the whole app) cancels any pending poll before it can replace the session.
        _closed = true;
        StopPolling();
        CancelImport();
        _polls.Cancel();
        _polls.Dispose();
    }
}
