using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using WallBili.Infrastructure.Storage;
using WallBili.Player.Mpv;

return await Probe.RunAsync(args);

internal static class Probe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
                WallBili content and queue probe (.NET 10)
                video INPUT                         Metadata and all BVID/CID parts
                media INPUT [--page N]              Resolve twice; safe format summary
                collection season|series UP_ID ID [--page N] [--size N]
                check-playback INPUT [--page N] [--mpv PATH] [--headless]
                check-queue INPUT1 INPUT2 [--mpv PATH] [--headless]
                INPUT: a BVID or https://www.bilibili.com/video/BVID/?p=N
                check-playback verifies decoded A/V, clock, pause, resume and seek.
                check-queue imports two video sources and seeks near each end to verify actual EOF advancement.
                --headless uses null outputs; visible/audible output still needs Windows.
                No session cookies, signed stream URLs or raw HTTP errors are printed.
                """);
            return 0;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; timeout.Cancel(); };
        using var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        }) { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            ValidateOptions(args);
            var bili = new BilibiliClient(client);
            switch (args[0])
            {
                case "video":
                {
                    var video = await bili.GetVideoAsync(Required(args, 1), timeout.Token);
                    Write(new { capability = "metadata", status = "passed", video });
                    return 0;
                }
                case "media":
                {
                    var (video, part) = await SelectPartAsync(bili, args, timeout.Token);
                    var first = await bili.ResolveMediaAsync(part.Key, timeout.Token);
                    var second = await bili.ResolveMediaAsync(part.Key, timeout.Token);
                    Write(new
                    {
                        capability = "media_resolution", status = "passed", video.Title,
                        part.Page, part.Key, first = Summary(first), second = Summary(second),
                        resolved_again_from_identity = first.Content == second.Content,
                        natural_expiry_verified = false, decoded = false
                    });
                    return 0;
                }
                case "collection":
                {
                    var kind = Required(args, 1) switch
                    {
                        "season" => CollectionKind.Season,
                        "series" => CollectionKind.Series,
                        _ => throw new ArgumentException("Collection type must be season or series.")
                    };
                    var source = new CollectionSource(PositiveLong(Required(args, 2)), PositiveLong(Required(args, 3)), kind);
                    var page = await bili.GetCollectionPageAsync(source, OptionInt(args, "--page", 1), OptionInt(args, "--size", 30), timeout.Token);
                    Write(new { capability = "collection_page", status = "passed", page });
                    return 0;
                }
                case "check-playback":
                {
                    var (_, part) = await SelectPartAsync(bili, args, timeout.Token);
                    var session = await bili.ResolveMediaAsync(part.Key, timeout.Token);
                    bool headless = args.Contains("--headless");
                    await using var player = new MpvPlayer(new MpvPlayerOptions
                    {
                        ExecutablePath = Option(args, "--mpv") ?? Environment.GetEnvironmentVariable("MPV_PATH") ?? "mpv",
                        Headless = headless,
                        StartPaused = true
                    });
                    await player.StartAsync(timeout.Token);
                    await player.LoadAsync(session, timeout.Token);
                    var videoParams = await WaitForPropertyAsync(player, "video-params", timeout.Token);
                    var audioParams = await WaitForPropertyAsync(player, "audio-params", timeout.Token);
                    var videoCodec = await player.GetPropertyAsync<string>("video-codec", timeout.Token);
                    var audioCodec = await player.GetPropertyAsync<string>("audio-codec", timeout.Token);
                    await player.ResumeAsync(timeout.Token);
                    var started = await WaitForPositionAsync(player, 0.15, timeout.Token);
                    await player.PauseAsync(timeout.Token);
                    await Task.Delay(200, timeout.Token);
                    double pausedAt = await PositionAsync(player, timeout.Token);
                    await Task.Delay(450, timeout.Token);
                    double stillAt = await PositionAsync(player, timeout.Token);
                    if (Math.Abs(stillAt - pausedAt) > 0.15)
                        throw new InvalidOperationException("Playback clock advanced while paused.");
                    double duration = (await player.GetPropertyAsync<JsonElement>("duration", timeout.Token)).GetDouble();
                    double seekTarget = Math.Min(2, duration / 2);
                    if (seekTarget < 0.4) throw new InvalidOperationException("Sample is too short for a meaningful seek check.");
                    await player.SeekAsync(TimeSpan.FromSeconds(seekTarget), timeout.Token);
                    double seekAt = await WaitForSeekAsync(player, seekTarget, timeout.Token);
                    await player.ResumeAsync(timeout.Token);
                    double resumedAt = await WaitForPositionAsync(player, seekAt + 0.15, timeout.Token);
                    await player.StopAsync(timeout.Token);
                    var renewed = await bili.ResolveMediaAsync(part.Key, timeout.Token);
                    Write(new
                    {
                        capability = "playback_controls", status = "passed", part.Page, part.Key,
                        media = Summary(session), video_params = videoParams, audio_params = audioParams,
                        video_codec = videoCodec, audio_codec = audioCodec,
                        started_at = started, paused_at = pausedAt, still_at = stillAt,
                        seek_target = seekTarget, seek_at = seekAt, resumed_at = resumedAt,
                        decoded_audio_and_video = true, headless,
                        visible_and_audible_output_verified = false,
                        output_acceptance = "requires human observation on Windows",
                        resolved_again_from_identity = renewed.Content == session.Content,
                        natural_expiry_verified = false
                    });
                    return 0;
                }
                case "check-queue":
                {
                    var directory = Path.Combine(Path.GetTempPath(), "wallbili-queue-probe-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    try
                    {
                        var store = new SqliteLibraryStore(Path.Combine(directory, "library.db"));
                        await using (var library = new LibrarySession(bili, store, new MpvQueuePlayer(() => new()
                        {
                            ExecutablePath = Option(args, "--mpv") ?? Environment.GetEnvironmentVariable("MPV_PATH") ?? "mpv",
                            Headless = args.Contains("--headless")
                        })))
                        {
                            await library.InitializeAsync(timeout.Token);
                            await library.ImportVideoAsync(Required(args, 1), timeout.Token);
                            await library.ImportVideoAsync(Required(args, 2), timeout.Token);
                            if (library.Snapshot.Sources.Count != 2) throw new ArgumentException("Use two distinct video sources.");
                            // One part from each selected source keeps this verification short and repeatable.
                            var keys = library.Snapshot.Sources.Select(s => s.Members.First()).ToArray();
                            await library.EnqueueAsync(keys, timeout.Token);
                            await library.PlayAsync(token: timeout.Token);
                            var tested = new HashSet<Guid>();
                            while (library.Snapshot.Playback.Intent != PlaybackIntent.Stopped)
                            {
                                var snapshot = library.Snapshot;
                                if (snapshot.Playback.CurrentId is { } id && tested.Add(id) && snapshot.Current is { } entry)
                                {
                                    if (entry.Duration.TotalSeconds < 3) throw new InvalidOperationException("Queue sample is too short.");
                                    await library.SeekAsync(entry.Duration - TimeSpan.FromSeconds(2), timeout.Token);
                                }
                                await Task.Delay(100, timeout.Token);
                            }
                            if (library.Snapshot.Queue.Any(q => q.CompletedCount != 1))
                                throw new InvalidOperationException("Not all queue entries completed successfully.");
                        }
                        var restored = await store.LoadAsync(timeout.Token);
                        Write(new { capability = "persistent_queue", status = "passed", sources = restored.Sources.Count,
                            entries = restored.Entries.Count, queue = restored.Queue.Select(q => new { q.Key, q.CompletedCount }),
                            eof_driven_advancement = true, seek_near_end = true, headless = args.Contains("--headless"),
                            windows_output_verified = false, temporary_database = true });
                    }
                    finally { Directory.Delete(directory, recursive: true); }
                    return 0;
                }
                default:
                    throw new ArgumentException("Unknown command; use --help.");
            }
        }
        catch (ContentAccessException error)
        {
            Write(new { status = "failed", kind = error.Kind.ToString(), message = error.Message });
            return 2;
        }
        catch (MpvException error)
        {
            Write(new { status = "failed", kind = "player_error", code = error.Code.ToString() });
            return 3;
        }
        catch (ArgumentException)
        {
            Write(new { status = "failed", kind = "invalid_arguments", message = "Check command arguments using --help." });
            return 1;
        }
        catch (OperationCanceledException)
        {
            Write(new { status = "failed", kind = "cancelled_or_timeout" });
            return 2;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or HttpRequestException or JsonException)
        {
            Write(new { status = "failed", kind = "verification_or_transport_error", error_type = error.GetType().Name });
            return 3;
        }
    }

    private static async Task<(VideoInfo Video, VideoPart Part)> SelectPartAsync(BilibiliClient bili, string[] args, CancellationToken ct)
    {
        string input = Required(args, 1);
        var parsed = BilibiliInput.Parse(input);
        var video = await bili.GetVideoAsync(input, ct);
        int selected = OptionInt(args, "--page", parsed.Page);
        var part = video.Parts.SingleOrDefault(x => x.Page == selected)
            ?? throw new ArgumentException("The requested part does not exist.");
        return (video, part);
    }

    // This is intentionally a projection. Serializing MediaSession would expose signed CDN URLs.
    private static object Summary(MediaSession session) => new
    {
        session.Content, session.ResolvedAt, session.ExpiresAt,
        video = TrackSummary(session.Video),
        audio = session.Audio is null ? null : TrackSummary(session.Audio),
        request_header_names = session.Headers.Keys.Order().ToArray()
    };

    private static object TrackSummary(MediaTrack track) => new
    {
        host = track.Url.Host, track.Codec, track.Quality, track.Width, track.Height, track.Bandwidth
    };

    private static async Task<JsonElement> WaitForPropertyAsync(MpvPlayer player, string name, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(35))
        {
            try
            {
                var value = await player.GetPropertyAsync<JsonElement>(name, ct);
                if (value.ValueKind == JsonValueKind.Object) return value;
            }
            catch (MpvException) { }
            await Task.Delay(150, ct);
        }
        throw new InvalidOperationException("Timed out waiting for decoded media parameters.");
    }

    private static async Task<double> PositionAsync(MpvPlayer player, CancellationToken ct) =>
        (await player.GetPropertyAsync<JsonElement>("time-pos", ct)).GetDouble();

    private static async Task<double> WaitForPositionAsync(MpvPlayer player, double target, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(20))
        {
            try
            {
                double value = await PositionAsync(player, ct);
                if (value >= target) return value;
            }
            catch (MpvException) { }
            await Task.Delay(100, ct);
        }
        throw new InvalidOperationException("Playback clock did not advance.");
    }

    private static async Task<double> WaitForSeekAsync(MpvPlayer player, double target, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(20))
        {
            double value = await PositionAsync(player, ct);
            if (Math.Abs(value - target) < 0.2) return value;
            await Task.Delay(100, ct);
        }
        throw new InvalidOperationException("Playback did not seek to the requested position.");
    }

    private static void ValidateOptions(string[] args)
    {
        var allowed = args[0] switch
        {
            "video" => Array.Empty<string>(),
            "media" => new[] { "--page" },
            "collection" => new[] { "--page", "--size" },
            "check-playback" => new[] { "--page", "--mpv", "--headless" },
            "check-queue" => new[] { "--mpv", "--headless" },
            _ => throw new ArgumentException("Unknown command.")
        };
        int positionalCount = args[0] switch { "collection" => 4, "check-queue" => 3, _ => 2 };
        if (args.Length < positionalCount) throw new ArgumentException("Missing arguments.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = positionalCount; i < args.Length; i++)
        {
            string flag = args[i];
            if (!allowed.Contains(flag) || !seen.Add(flag)) throw new ArgumentException("Unknown or duplicate option.");
            if (flag != "--headless") _ = Required(args, ++i);
        }
        // Reject malformed numeric options before contacting the service.
        _ = OptionInt(args, "--page", 1);
        _ = OptionInt(args, "--size", 30);
    }

    private static string Required(string[] args, int index) =>
        index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal) ? args[index] : throw new ArgumentException("Missing argument.");

    private static string? Option(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        return index < 0 ? null : Required(args, index + 1);
    }

    private static int OptionInt(string[] args, string key, int fallback)
    {
        if (Option(args, key) is not { } value) return fallback;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0
            ? number : throw new ArgumentException("Expected a positive 32-bit integer.");
    }

    private static long PositiveLong(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number) && number > 0
            ? number : throw new ArgumentException("Expected a positive integer.");

    private static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}
