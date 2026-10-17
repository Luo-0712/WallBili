using System.Windows;
using System.Windows.Threading;
using WallBili.Core;
using Xunit;

namespace WallBili.Desktop.Tests;

public sealed class SubscriptionDialogTests
{
    [Fact]
    public Task FilteringAndLoadingPagesKeepsSubscriptionsOutsideTheVisibleList() => OnSta(async () =>
    {
        var client = new Discovery(page => Task.FromResult(page == 1
            ? new FollowingsPage(1, 30, 60, [new(10, "Alpha"), new(20, "Beta")])
            : new(2, 30, 60, [new(30, "Gamma")])));
        var dialog = new SubscriptionDialog(client, [new(99, "Unloaded"), new(10, "Alpha")]);
        try
        {
            await dialog.LoadNextPageAsync();
            dialog.FilterInput.Text = "Alpha";
            Assert.Single(dialog.FollowingList.Items.Cast<object>());

            await dialog.LoadNextPageAsync();

            Assert.Single(dialog.FollowingList.Items.Cast<object>());
            Assert.Equal([10L, 99L], dialog.SelectedCreators.Select(c => c.Uid).Order());
            Assert.False(dialog.MoreButton.IsEnabled);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task ATemporaryFailureRetriesTheSamePageWithoutDiscardingChoices() => OnSta(async () =>
    {
        var pages = new List<int>();
        var fail = true;
        var client = new Discovery(page =>
        {
            pages.Add(page);
            if (fail) return Task.FromException<FollowingsPage>(new ContentAccessException(ContentFailureKind.RateLimited, "rate limited"));
            return Task.FromResult(new FollowingsPage(page, 30, 1, [new(10, "Alpha")]));
        });
        var dialog = new SubscriptionDialog(client, [new(99, "Unloaded")]);
        try
        {
            await dialog.LoadNextPageAsync();
            Assert.Contains("限制", dialog.PageStatus.Text);
            Assert.True(dialog.MoreButton.IsEnabled);
            fail = false;

            await dialog.LoadNextPageAsync();

            Assert.Equal([1, 1], pages);
            Assert.Equal(99, Assert.Single(dialog.SelectedCreators).Uid);
            Assert.Contains("加载完毕", dialog.PageStatus.Text);
        }
        finally { dialog.Close(); }
    });

    private static async Task OnSta(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    private sealed class Discovery(Func<int, Task<FollowingsPage>> pages) : IContentDiscoveryClient
    {
        public Task<FollowingsPage> GetFollowingsAsync(int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) => pages(page);
        public Task<CreatorVideosPage> GetCreatorVideosAsync(long uploaderId, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
