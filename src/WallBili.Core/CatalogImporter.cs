namespace WallBili.Core;

public sealed record CatalogImport(CatalogSource Source, IReadOnlyList<CatalogEntry> Entries);

public sealed class CatalogImporter(IContentClient content)
{
    public async Task<CatalogImport> VideoAsync(string input, CancellationToken token = default)
    {
        var video = await content.GetVideoAsync(input, token);
        var entries = Expand(video);
        return new(new("video:" + video.Bvid, video.Title, video.Bvid, null, DateTimeOffset.UtcNow,
            entries.Select(e => e.Key).ToArray(), []), entries);
    }

    public async Task<CatalogImport> CollectionAsync(CollectionSource source, CancellationToken token = default)
    {
        var entries = new List<CatalogEntry>();
        var problems = new List<ImportProblem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string title = "";
        // A changing or malformed remote collection must never create an endless import.
        for (var page = 1; page <= 1000; page++)
        {
            var result = await content.GetCollectionPageAsync(source, page, 30, token);
            title = result.Title;
            if (result.Page != page || result.PageSize <= 0 || (result.HasMore && result.Entries.Count == 0))
                throw new ContentAccessException(ContentFailureKind.InvalidResponse, "Invalid collection pagination.");
            var added = 0;
            foreach (var item in result.Entries)
            {
                if (!seen.Add(item.Bvid)) continue;
                added++;
                try { entries.AddRange(Expand(await content.GetVideoAsync(item.Bvid, token))); }
                catch (ContentAccessException ex) when (ex.Kind is ContentFailureKind.Removed or
                    ContentFailureKind.Inaccessible or ContentFailureKind.AuthenticationRequired or ContentFailureKind.Unsupported)
                {
                    problems.Add(new(item.Bvid, ex.Kind.ToString()));
                }
            }
            if (page > 1 && added == 0)
                throw new ContentAccessException(ContentFailureKind.InvalidResponse, "Collection pagination repeated without progress.");
            if (!result.HasMore)
                return new(new($"{source.Kind}:{source.UploaderId}:{source.CollectionId}", title, null, source,
                    DateTimeOffset.UtcNow, entries.Select(e => e.Key).Distinct().ToArray(), problems),
                    entries.DistinctBy(e => e.Key).ToArray());
        }
        throw new ContentAccessException(ContentFailureKind.InvalidResponse, "Collection exceeded the import page limit.");
    }

    private static CatalogEntry[] Expand(VideoInfo video) => video.Parts.Select(p =>
        new CatalogEntry(p.Key, video.Parts.Count == 1 ? video.Title : $"{video.Title} · {p.Title}",
            video.Uploader, p.Page, p.Duration)).DistinctBy(e => e.Key).ToArray();
}
