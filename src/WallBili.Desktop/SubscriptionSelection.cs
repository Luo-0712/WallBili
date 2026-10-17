using WallBili.Core;

namespace WallBili.Desktop;

/// <summary>Keeps explicit choices across following pages and name filters. An unloaded following is
/// never treated as an unchecked subscription.</summary>
internal sealed class SubscriptionSelection(IEnumerable<FollowedCreator> subscriptions)
{
    private readonly Dictionary<long, FollowedCreator> _saved = subscriptions.ToDictionary(c => c.Uid);
    private readonly List<CreatorSelectionRow> _rows = [];
    public IReadOnlyList<CreatorSelectionRow> Rows => _rows;

    public void RemoveSubscription(long uid)
    {
        _saved.Remove(uid);
        var row = _rows.FirstOrDefault(r => r.Creator.Uid == uid);
        if (row is not null) row.IsSelected = false;
    }

    public void AddPage(IEnumerable<FollowedCreator> creators)
    {
        foreach (var creator in creators)
        {
            if (_rows.Any(row => row.Creator.Uid == creator.Uid)) continue;
            _rows.Add(new(creator, _saved.ContainsKey(creator.Uid)));
        }
    }

    public IReadOnlyList<FollowedCreator> SelectedCreators
    {
        get
        {
            var selected = new Dictionary<long, FollowedCreator>(_saved);
            foreach (var row in _rows)
            {
                if (row.IsSelected) selected[row.Creator.Uid] = row.Creator;
                else selected.Remove(row.Creator.Uid);
            }
            return selected.Values.ToArray();
        }
    }
}

internal sealed class CreatorSelectionRow(FollowedCreator creator, bool selected)
{
    public FollowedCreator Creator { get; } = creator;
    public bool IsSelected { get; set; } = selected;
}
