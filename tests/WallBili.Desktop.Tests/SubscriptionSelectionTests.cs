using WallBili.Core;
using WallBili.Desktop;
using Xunit;

namespace WallBili.Desktop.Tests;

public sealed class SubscriptionSelectionTests
{
    [Fact]
    public void SavingAPartiallyLoadedFollowingListRetainsUnloadedSubscriptions()
    {
        var selection = new SubscriptionSelection([new(10, "Unloaded"), new(20, "Visible")]);
        selection.AddPage([new(20, "Visible"), new(30, "New following")]);
        selection.Rows.Single(r => r.Creator.Uid == 20).IsSelected = false;
        selection.Rows.Single(r => r.Creator.Uid == 30).IsSelected = true;

        Assert.Equal([10L, 30L], selection.SelectedCreators.Select(c => c.Uid).Order());
    }

    [Fact]
    public void RemovingASavedCreatorIsPossibleBeforeTheirFollowingPageIsLoaded()
    {
        var selection = new SubscriptionSelection([new(10, "No longer followed")]);

        selection.RemoveSubscription(10);

        Assert.Empty(selection.SelectedCreators);
    }

    [Fact]
    public void RetryingAnOverlappingPagePreservesExplicitChoicesAndDoesNotSelectNewFollowings()
    {
        var selection = new SubscriptionSelection([]);
        selection.AddPage([new(10, "First"), new(20, "Second")]);
        selection.Rows[0].IsSelected = true;

        selection.AddPage([new(10, "First"), new(30, "Third")]);

        Assert.Equal(3, selection.Rows.Count);
        Assert.Equal(10, Assert.Single(selection.SelectedCreators).Uid);
        Assert.False(selection.Rows.Single(r => r.Creator.Uid == 30).IsSelected);
    }
}
