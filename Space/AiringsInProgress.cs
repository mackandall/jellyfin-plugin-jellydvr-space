using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.DvrSpace.Space;

/// <summary>
/// The programmes Jellyfin is recording now, from its own timer store, and the pieces Episode
/// History is still to join. A recording cut by a restart goes on in another file once Jellyfin
/// resumes it; until then no file is being written, yet its pieces must stay put: moving them lets
/// the resumed piece land on a name that's taken, and leaves the joiner without its pieces.
/// </summary>
internal sealed class AiringsInProgress
{
    // A join that hasn't happened by then (failed, or joining turned off) no longer holds the pieces.
    private static readonly TimeSpan JoinWaitLimit = TimeSpan.FromHours(2);

    private readonly List<Airing> _airings;
    private readonly HashSet<string> _toJoin;

    private AiringsInProgress(List<Airing> airings, HashSet<string> toJoin)
    {
        _airings = airings;
        _toJoin = toJoin;
    }

    private sealed record Airing(string Name, int? Season, int? Episode, bool IsMovie);

    /// <summary>
    /// Timers marked in progress, or not cancelled and within their padded start and end now; and
    /// Episode History's split recordings not joined yet.
    /// </summary>
    public static AiringsInProgress Read(IConfigurationManager config) =>
        new(ReadTimers(config), ReadPiecesToJoin(config));

    /// <summary>Why the item must stay where it is for now, or null.</summary>
    public string? Why(BaseItem item) =>
        item.Path is { } path && _toJoin.Contains(path) ? "a piece of a split recording, waiting to be joined"
        : Includes(item) ? "its programme is still recording (in another piece)"
        : null;

    private static List<Airing> ReadTimers(IConfigurationManager config)
    {
        var result = new List<Airing>();
        try
        {
            var file = Path.Combine(config.CommonApplicationPaths.DataPath, "livetv", "timers.json");
            if (!File.Exists(file))
            {
                return result;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var now = DateTime.UtcNow;
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var status = Str(t, "Status");
                var start = Date(t, "StartDate").AddSeconds(-Int(t, "PrePaddingSeconds"));
                var end = Date(t, "EndDate").AddSeconds(Int(t, "PostPaddingSeconds"));
                var going = status is "InProgress" or "2"
                    || (status is "New" or "0" && start <= now && now <= end);
                if (going && Str(t, "Name") is { Length: > 0 } name)
                {
                    result.Add(new Airing(
                        name,
                        IntOrNull(t, "SeasonNumber"),
                        IntOrNull(t, "EpisodeNumber"),
                        t.TryGetProperty("IsMovie", out var m) && m.ValueKind == JsonValueKind.True));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    // Episode History's programmes.json: airings with two or more pieces on disk that it will join
    // (undecided, not joined, tries left), as its joiner checks them.
    private static HashSet<string> ReadPiecesToJoin(IConfigurationManager config)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var file = Path.Combine(config.CommonApplicationPaths.DataPath, "episode-history", "programmes.json");
            if (!File.Exists(file))
            {
                return result;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            var now = DateTime.UtcNow;
            foreach (var p in doc.RootElement.EnumerateObject().Select(x => x.Value))
            {
                if (p.ValueKind != JsonValueKind.Object
                    || (Str(p, "State") ?? string.Empty) != string.Empty
                    || !string.IsNullOrEmpty(Str(p, "JoinedPath"))
                    || Int(p, "JoinTries") >= 3
                    || now - Date(p, "ProgramEndUtc") > JoinWaitLimit
                    || !p.TryGetProperty("Pieces", out var pieces) || pieces.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var files = pieces.EnumerateArray().Select(x => Str(x, "Path")).OfType<string>().Where(File.Exists).ToList();
                if (files.Count >= 2)
                {
                    result.UnionWith(files);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    /// <summary>Whether the item is (a piece of) a programme being recorded now.</summary>
    private bool Includes(BaseItem item) => _airings.Any(a => item switch
    {
        Episode e => Same(e.SeriesName, a.Name)
            && (a.Season is null || e.ParentIndexNumber == a.Season)
            && (a.Episode is null || e.IndexNumber == a.Episode),
        _ => Same(item.Name, a.Name)
    });

    private static bool Same(string? x, string y) => string.Equals(x?.Trim(), y.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    private static DateTime Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var d) ? d.ToUniversalTime() : DateTime.MinValue;

    private static int Int(JsonElement e, string name) => IntOrNull(e, name) ?? 0;

    private static int? IntOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
