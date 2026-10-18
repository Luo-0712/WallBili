using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using WallBili.Core;

namespace WallBili.Desktop;

public partial class SubscriptionDialog : Window
{
    private readonly IContentDiscoveryClient _client;
    private readonly SubscriptionSelection _selection;
    private readonly CancellationTokenSource _lifetime = new();
    private int _nextPage = 1;
    private bool _loading, _hasMore = true, _closed;
    internal IReadOnlyList<FollowedCreator> SelectedCreators => _selection.SelectedCreators;

    public SubscriptionDialog(IContentDiscoveryClient client, IEnumerable<FollowedCreator> subscriptions)
    {
        _client = client;
        _selection = new(subscriptions);
        InitializeComponent();
        RenderSelection();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadNextPageAsync();
    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await LoadNextPageAsync();

    internal async Task LoadNextPageAsync()
    {
        if (_loading || !_hasMore || _closed) return;
        _loading = true;
        MoreButton.IsEnabled = false;
        PageStatus.Text = $"正在读取第 {_nextPage} 页关注…";
        try
        {
            var page = await _client.GetFollowingsAsync(_nextPage, 30, _lifetime.Token);
            if (_closed) return;
            _selection.AddPage(page.Items);
            _hasMore = page.HasMore;
            _nextPage = page.Page + 1;
            ApplyFilter();
            PageStatus.Text = $"已加载 {_selection.Rows.Count} / {page.Total} 位关注" +
                (_hasMore ? "，可继续加载更多。" : "，关注列表已加载完毕。");
            MoreButton.Content = _hasMore ? "加载更多关注" : "已加载全部关注";
        }
        catch (OperationCanceledException) when (_closed || _lifetime.IsCancellationRequested) { }
        catch (ContentAccessException ex)
        {
            PageStatus.Text = ex.Kind switch
            {
                ContentFailureKind.AuthenticationRequired => "需要重新登录 B 站后读取关注；已有选择仍然保留。",
                ContentFailureKind.RateLimited => "B 站暂时限制了请求，请稍后重试。已有选择仍然保留。",
                _ => "关注列表读取失败，可重试当前页；已有选择仍然保留。"
            };
            MoreButton.Content = "重试当前页";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            PageStatus.Text = "网络请求失败或超时，可重试当前页；已有选择仍然保留。";
            MoreButton.Content = "重试当前页";
        }
        finally
        {
            _loading = false;
            MoreButton.IsEnabled = !_closed && _hasMore;
            if (_closed) _lifetime.Dispose();
        }
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        if (FollowingList is not null) ApplyFilter();
    }
    private void ApplyFilter()
    {
        var query = FilterInput.Text.Trim();
        FollowingList.ItemsSource = _selection.Rows.Where(row => row.Creator.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void Selection_Click(object sender, RoutedEventArgs e) => RenderSelection();
    private void RenderSelection()
    {
        var selected = _selection.SelectedCreators;
        SavedList.ItemsSource = selected;
        SelectedTitle.Text = $"已选订阅（{selected.Count}）";
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SavedList.SelectedItem is not FollowedCreator creator) return;
        _selection.RemoveSubscription(creator.Uid);
        // Recreate the checkboxes after a removal so an already loaded following is unchecked too.
        FollowingList.ItemsSource = null;
        ApplyFilter();
        RenderSelection();
    }
    private void Save_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        // A pending request disposes its token source after unwinding; otherwise release it now.
        if (!_loading) _lifetime.Dispose();
    }
}
