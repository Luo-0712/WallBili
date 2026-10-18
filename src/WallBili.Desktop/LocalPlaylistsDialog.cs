using System.Windows;
using System.Windows.Controls;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;

namespace WallBili.Desktop;

/// <summary>The desktop uses the exact same application commands as MCP. Preparing a large playlist
/// leaves the dialog responsive and all progress comes from the shared LibrarySession snapshot.</summary>
internal sealed class LocalPlaylistsDialog : Window
{
    private readonly Func<LibrarySnapshot> _snapshot;
    private readonly Func<string, object, Task<AgentResult>> _invoke;
    private readonly ListBox _playlists, _members;
    private readonly TextBox _name, _references;
    private readonly TextBlock _summary, _notice;
    private readonly Grid _body;
    private Guid? _selected;
    private bool _rendering, _working, _closed;

    public LocalPlaylistsDialog(Func<LibrarySnapshot> snapshot, Func<string, object, Task<AgentResult>> invoke)
    {
        _snapshot = snapshot; _invoke = invoke;
        AgentDialogUi.Window(this, "WallBili · 本地片单", 1050, 750);
        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(AgentDialogUi.Text("本地片单", "Text.PageTitle"));
        var subtitle = AgentDialogUi.Text("保存一组内容和顺序，随时继续播放。助手创建的片单也会显示在这里。", "Text.Body");
        subtitle.Margin = new Thickness(0, 12, 0, 22); header.Children.Add(subtitle); root.Children.Add(header);
        _body = new Grid(); Grid.SetRow(_body, 1); root.Children.Add(_body);
        _body.ColumnDefinitions.Add(new() { Width = new GridLength(255) });
        _body.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
        _body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel();
        var add = AgentDialogUi.Button("新建片单", Create_Click, true); add.Margin = new Thickness(0, 0, 0, 14);
        DockPanel.SetDock(add, Dock.Top); left.Children.Add(add);
        _playlists = new ListBox { Name = "PlaylistList", DisplayMemberPath = nameof(PlaylistRow.Label), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AgentDialogUi.Style(_playlists, "List.Rows");
        _playlists.SelectionChanged += (_, _) =>
        {
            if (_rendering) return;
            _selected = (_playlists.SelectedItem as PlaylistRow)?.Id;
            Refresh(_snapshot(), selectionChanged: true);
        };
        left.Children.Add(_playlists); _body.Children.Add(left);

        var right = new Grid(); Grid.SetColumn(right, 2); _body.Children.Add(right);
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        right.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var editor = new DockPanel();
        var rename = AgentDialogUi.Button("重命名", Rename_Click); rename.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(rename, Dock.Right); editor.Children.Add(rename);
        _name = new TextBox { Name = "PlaylistNameInput", MinWidth = 170 }; AgentDialogUi.Style(_name, "Input.Plain"); editor.Children.Add(_name);
        right.Children.Add(editor);
        _summary = AgentDialogUi.Text(""); _summary.Margin = new Thickness(0, 14, 0, 14);
        Grid.SetRow(_summary, 1); right.Children.Add(_summary);
        _members = new ListBox { Name = "PlaylistMembersList", DisplayMemberPath = nameof(MemberRow.Label), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AgentDialogUi.Style(_members, "List.Rows");
        Grid.SetRow(_members, 2); right.Children.Add(_members);
        var memberActions = new WrapPanel { Margin = new Thickness(0, 14, 0, 18) };
        memberActions.Children.Add(AgentDialogUi.Button("替换队列并播放", Play_Click, true));
        memberActions.Children.Add(AgentDialogUi.Button("加入队列", Enqueue_Click));
        memberActions.Children.Add(AgentDialogUi.Button("上移", (_, _) => Move(-1)));
        memberActions.Children.Add(AgentDialogUi.Button("下移", (_, _) => Move(1)));
        memberActions.Children.Add(AgentDialogUi.Button("移除成员", Remove_Click));
        Grid.SetRow(memberActions, 3); right.Children.Add(memberActions);
        var bottom = new StackPanel(); Grid.SetRow(bottom, 4); right.Children.Add(bottom);
        bottom.Children.Add(AgentDialogUi.Text("追加内容 · 每行一个 BV 号、视频链接（全部分 P）或合集 / 系列链接", "Text.Label"));
        _references = new TextBox { Name = "PlaylistReferencesInput", AcceptsReturn = true, Height = 66, MinHeight = 66, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 10, 0, 12), TextWrapping = TextWrapping.Wrap };
        AgentDialogUi.Style(_references, "Input.Area"); bottom.Children.Add(_references);
        var actions = new WrapPanel();
        actions.Children.Add(AgentDialogUi.Button("追加到片单", Append_Click));
        actions.Children.Add(AgentDialogUi.Button("刷新来源", Refresh_Click));
        actions.Children.Add(AgentDialogUi.Button("取消整理", Cancel_Click));
        actions.Children.Add(AgentDialogUi.Button("重试整理", Retry_Click));
        actions.Children.Add(AgentDialogUi.Button("删除片单", Delete_Click)); bottom.Children.Add(actions);
        _notice = AgentDialogUi.Text("片单删除后，已经加入播放队列的内容会继续保留。", "Text.Quiet");
        _notice.Margin = new Thickness(0, 18, 0, 0); Grid.SetRow(_notice, 2); root.Children.Add(_notice);
        Content = root;
        Closed += (_, _) => _closed = true;
        Refresh(snapshot());
    }

    public void Refresh(LibrarySnapshot snapshot) => Refresh(snapshot, selectionChanged: false);
    private void Refresh(LibrarySnapshot snapshot, bool selectionChanged)
    {
        if (_closed || _rendering) return;
        _rendering = true;
        try
        {
            var playlists = snapshot.Agent?.Playlists ?? [];
            var selected = playlists.FirstOrDefault(p => p.Id == _selected) ?? playlists.FirstOrDefault();
            var selectedMember = (_members.SelectedItem as MemberRow)?.Id;
            var rows = playlists.Select(p => new PlaylistRow(p.Id, $"{p.Name} · {p.Members.Sum(m => m.Parts.Length)} 项")).ToArray();
            _playlists.ItemsSource = rows;
            _playlists.SelectedItem = rows.FirstOrDefault(p => p.Id == selected?.Id);
            if (_selected != selected?.Id) selectionChanged = true;
            _selected = selected?.Id;
            if (selectionChanged || !_name.IsKeyboardFocused) _name.Text = selected?.Name ?? "新片单";
            var task = snapshot.Agent?.Tasks.FirstOrDefault(t => t.Id == selected?.TaskId);
            _summary.Text = selected is null ? "还没有片单，点击新建片单开始。"
                : $"{selected.Members.Length} 个成员 · {selected.Members.Sum(m => m.Parts.Length)} 个已准备分 P" +
                  (task is null ? "" : $"\n{TaskState(task.State)} · 已处理 {task.Processed}" + (task.Total is { } total ? $" / {total}" : "")) +
                  (task?.Failure is { } failure ? $"\n{DescribeFailure(failure)}" : "");
            var members = (selected?.Members ?? []).Select((m, i) => new MemberRow(m.Id,
                $"{i + 1:00}   {m.Title}  ·  {(m.Failure is { } error ? DescribeFailure(error) : m.Parts.Length > 0 ? m.Parts.Length + " 个分 P" : "正在准备")}")).ToArray();
            _members.ItemsSource = members;
            _members.SelectedItem = members.FirstOrDefault(m => m.Id == selectedMember);
        }
        finally { _rendering = false; }
    }

    private LocalPlaylist? Selected => _snapshot().Agent?.Playlists.FirstOrDefault(p => p.Id == _selected);
    private static string DescribeFailure(string failure) => failure switch
    {
        "Removed" => "内容已失效", "Inaccessible" => "内容暂不可访问", "AuthenticationRequired" => "请登录有权限的账号后重试",
        "RateLimited" => "请求较频繁，请稍后重试", "Network" => "网络暂不可用，请检查连接后重试",
        "SessionChanged" => "账号已切换，请重试整理", "Interrupted" => "整理已中断，可重试继续",
        "Cancelled" => "已取消整理", "Unsupported" => "暂不支持此内容", _ => "整理未完成，请稍后重试"
    };
    private string RequestId() => "desktop-" + Guid.NewGuid().ToString("N");
    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var result = await RunAsync("create_playlist", new { name = "新片单", references = Array.Empty<ContentRef>(), requestId = RequestId() });
        if (result is { IsError: false } && result.Data.TryGetProperty("playlistId", out var id) && id.TryGetGuid(out var playlistId))
        { _selected = playlistId; Refresh(_snapshot(), selectionChanged: true); }
    }
    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } playlist)
            await RunAsync("edit_playlist", new { playlistId = playlist.Id, action = "rename", name = _name.Text.Trim(),
                expectedRevision = playlist.Revision, requestId = RequestId() });
    }
    private async void Append_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } playlist) return;
        ContentRef[] references;
        try
        {
            references = _references.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseReference).ToArray();
            if (references.Length == 0) { _notice.Text = "请先输入要追加的视频或合集。"; return; }
        }
        catch (ContentAccessException) { _notice.Text = "请输入有效的 BV 号、B 站视频链接或 UP 主空间内的合集链接，每行一个。"; return; }
        var result = await RunAsync("edit_playlist", new { playlistId = playlist.Id, action = "append", references,
            expectedRevision = playlist.Revision, requestId = RequestId() });
        if (result is { IsError: false }) _references.Clear();
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } playlist && _members.SelectedItem is MemberRow member)
            await RunAsync("edit_playlist", new { playlistId = playlist.Id, action = "remove", memberIds = new[] { member.Id },
                expectedRevision = playlist.Revision, requestId = RequestId() });
    }
    private async void Move(int offset)
    {
        if (Selected is not { } playlist || _members.SelectedItem is not MemberRow member) return;
        var ids = playlist.Members.Select(m => m.Id).ToList();
        var index = ids.IndexOf(member.Id); var target = Math.Clamp(index + offset, 0, ids.Count - 1);
        if (index == target) return;
        ids.RemoveAt(index); ids.Insert(target, member.Id);
        await RunAsync("edit_playlist", new { playlistId = playlist.Id, action = "reorder", memberIds = ids,
            expectedRevision = playlist.Revision, requestId = RequestId() });
    }
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } playlist) return;
        var startIndex = _members.SelectedItem is MemberRow row
            ? playlist.Members.TakeWhile(m => m.Id != row.Id).Sum(m => m.Parts.Length) : 0;
        await RunAsync("load_playlist", new { playlistId = playlist.Id, mode = "replace", autoplay = true, startIndex,
            expectedRevision = _snapshot().Agent?.QueueRevision ?? 0, requestId = RequestId() });
    }
    private async void Enqueue_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } playlist) await RunAsync("load_playlist", new { playlistId = playlist.Id,
            mode = "append", autoplay = false, requestId = RequestId() });
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } playlist) await RunAsync("refresh_playlist", new { playlistId = playlist.Id, requestId = RequestId() });
    }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } playlist) await RunAsync("delete_playlist", new { playlistId = playlist.Id,
            expectedRevision = playlist.Revision, requestId = RequestId() });
    }
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.TaskId is { } taskId) await RunAsync("cancel_task", new { taskId, requestId = RequestId() });
    }
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.TaskId is { } taskId) await RunAsync("retry_task", new { taskId, requestId = RequestId() });
    }
    private async Task<AgentResult?> RunAsync(string tool, object arguments)
    {
        if (_working || _closed) return null;
        _working = true; _body.IsEnabled = false;
        try
        {
            var result = await _invoke(tool, arguments);
            if (!_closed)
            {
                _notice.Text = result.Summary;
                if (result.IsError && result.Data.TryGetProperty("error", out var error) && error.TryGetProperty("nextStep", out var next))
                    _notice.Text += " " + next.GetString();
                Refresh(_snapshot());
            }
            return result;
        }
        catch (OperationCanceledException) { if (!_closed) _notice.Text = "操作已取消。"; return null; }
        catch { if (!_closed) _notice.Text = "片单操作未完成，请检查应用数据目录和连接状态后重试。"; return null; }
        finally { _working = false; if (!_closed) _body.IsEnabled = true; }
    }
    private static ContentRef ParseReference(string input)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Host == "space.bilibili.com")
        {
            var collection = BilibiliCollectionInput.Parse(input);
            return new("collection", UploaderId: collection.UploaderId, CollectionId: collection.CollectionId, CollectionKind: collection.Kind);
        }
        return new("video", Bvid: BilibiliInput.Parse(input).Bvid);
    }
    private static string TaskState(string state) => state.ToLowerInvariant() switch
    {
        "pending" => "等待整理", "running" => "正在整理", "completed" => "整理完成", "failed" => "整理失败",
        "cancelled" or "canceled" => "已取消", "interrupted" => "已中断，可重试", _ => state
    };
    private sealed record PlaylistRow(Guid Id, string Label);
    private sealed record MemberRow(Guid Id, string Label);
}
