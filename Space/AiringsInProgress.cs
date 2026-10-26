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
/// The programmes Jellyfin is recording now, from its own timer store. A recording cut by a
/// restart goes on in another file once Jellyfin resumes it; until then no file is being written,
/// yet its pieces must stay put: moving them lets the resumed piece land on a name that's taken.
/// </summary>
internal sealed class AiringsInProgress
{
    private readonly List<Airing> _airings;

    private AiringsInProgress(List<Airing> airings) => _airings = airings;

    private sealed record Airing(string Name, int? Season, int? Episode, bool IsMovie);

    /// <summary>Timers marked in progress, or not cancelled and within their padded start and end now.</summary>
    public static AiringsInProgress Read(IConfigurationManager config)
    {
        var result = new List<Airing>();
        try
        {
            var file = Path.Combine(config.CommonApplicationPaths.DataPath, "livetv", "timers.json");
            if (!File.Exists(file))
            {
                return new AiringsInProgress(result);
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

        return new AiringsInProgress(result);
    }

    /// <summary>Whether the item is (a piece of) a programme being recorded now.</summary>
    public bool Includes(BaseItem item) => _airings.Any(a => item switch
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
