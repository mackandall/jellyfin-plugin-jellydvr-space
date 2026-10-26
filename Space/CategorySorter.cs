using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DvrSpace.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Jellyfin.Plugin.DvrSpace.Space.RecordingFileOps;

namespace Jellyfin.Plugin.DvrSpace.Space;

/// <summary>
/// Sorts recordings out of the DVR's general recording folder by guide category. Jellyfin only
/// routes movies and series to their own folders; everything else (specials, one-off games, …)
/// lands in the general folder. Each rule names guide categories and a destination; the first
/// rule a recording matches moves it. A rule can also take recordings from the series folder
/// (games have series ids, so they're filed as series) (with its sidecars, same safe copy-then-remove as archive
/// moves). Categories come from the item's genres and the guide .nfo Jellyfin writes beside it.
/// Before the rules, per-show locations: a show's recordings go to the first of its locations
/// that has room, or stay put when none has.
/// </summary>
public sealed class CategorySorter
{
    private readonly ILogger<CategorySorter> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly IConfigurationManager _config;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IServiceProvider _serviceProvider;

    public CategorySorter(
        ILogger<CategorySorter> logger,
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        IConfigurationManager config,
        ILibraryMonitor libraryMonitor,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _config = config;
        _libraryMonitor = libraryMonitor;
        _serviceProvider = serviceProvider;
    }

    /// <summary>The most recent sort plan, for the settings page.</summary>
    public SortPlan? LastPlan { get; private set; }

    public SortPlan BuildPlan(PluginConfiguration cfg)
    {
        var plan = new SortPlan { Mode = cfg.SortMode, CreatedUtc = DateTime.UtcNow };
        LastPlan = plan;
        if (cfg.SortMode == PluginConfiguration.SortOff)
        {
            return plan;
        }

        var tv = _config.GetConfiguration<LiveTvOptions>("livetv");
        var source = Clean(SpaceManager.RecordingPath(_config));
        if (source is null)
        {
            plan.Warning = "No recording path is set in Live TV settings.";
            return plan;
        }

        plan.Source = source;
        // Movie and series recordings already have their own folders; leave them alone even
        // if those folders sit inside the general one (unless a rule takes series recordings).
        var elsewhere = new[] { Clean(tv.MovieRecordingPath), Clean(tv.SeriesRecordingPath) }
            .OfType<string>()
            .Where(p => p != source)
            .ToList();
        var series = Clean(tv.SeriesRecordingPath) is { } sp && sp != source ? sp : null;
        plan.Rules = CheckRules(cfg, tv, source, series);
        plan.Shows = CheckShows(cfg, source, series);
        plan.KnownShows = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Series }, Recursive = true })
            .Select(s => s.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shows = plan.Shows.Where(s => s.Enabled).ToList();
        var fromSeries = series is not null && (plan.Rules.Any(r => r.Enabled && r.IncludeSeries) || shows.Count > 0);
        // Projected free space per show location as recordings are assigned to it.
        var projected = plan.Shows.SelectMany(s => s.Locations)
            .Where(l => l.FreeBytes is not null)
            .GroupBy(l => l.Path)
            .ToDictionary(g => g.Key, g => g.First().FreeBytes!.Value);

        var keep = new HashSet<string>(cfg.KeepItemIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var playing = _sessionManager.Sessions.Select(s => s.NowPlayingItem?.Id).OfType<Guid>().ToHashSet();
        var recordings = _serviceProvider.GetService<IRecordingsManager>();

        // Each recording with the folder it's sorted from (its sub-folders are kept below the destination).
        var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = fromSeries
                    ? new[] { BaseItemKind.Movie, BaseItemKind.Video, BaseItemKind.Episode }
                    : new[] { BaseItemKind.Movie, BaseItemKind.Video },
                IsVirtualItem = false,
                Recursive = true
            })
            .Select(i => (Item: i, From: i.Path is not { } p ? null
                : IsUnder(p, source) && !elsewhere.Any(e => IsUnder(p, e)) ? source
                : fromSeries && IsUnder(p, series!) ? series
                : null))
            .Where(x => x.From is not null)
            .GroupBy(x => x.Item.Path) // the same file can appear in more than one library
            .Select(g => g.First())
            .OrderBy(x => x.Item.DateCreated);

        foreach (var (item, from) in items.Where(x => !SpaceManager.IsLiveBuffer(x.Item))) // buffers stay put until kept
        {
            if (!File.Exists(item.Path))
            {
                continue; // just moved (the library still has the old path until it notices)
            }

            var isSeries = from == series;
            string? Busy(string path) =>
                keep.Contains(item.Id.ToString("N")) ? "marked Keep"
                : playing.Contains(item.Id) ? "being played"
                : recordings?.GetActiveRecordingInfo(path) is not null ? "still recording"
                : IsOpen(path) ? "in use (e.g. commercial detection still reading it); will retry"
                : null;

            if (ShowOf(item, from!) is { } showName && shows.FirstOrDefault(s => SameShow(s.Show, showName)) is { } show)
            {
                var file = item.Path!;
                var showEntry = new SortItem
                {
                    ItemId = item.Id,
                    Name = item is Episode ep && !string.IsNullOrEmpty(ep.SeriesName) ? ep.SeriesName + ": " + item.Name : item.Name,
                    Path = file,
                    Source = from!,
                    Rule = "Show: " + show.Show
                };
                showEntry.Why = Busy(file);
                if (showEntry.Why is null)
                {
                    var bytes = RecordingFiles(file).Sum(SafeLength);
                    var rel = Path.GetRelativePath(from!, Path.GetDirectoryName(file)!);
                    var notes = new List<string>();
                    foreach (var loc in show.Locations)
                    {
                        var dest = Path.GetFullPath(Path.Combine(loc.Path, rel));
                        if (loc.Problem is { } locProblem)
                        {
                            notes.Add($"{loc.Path}: {locProblem}");
                        }
                        else if (File.Exists(Path.Combine(dest, Path.GetFileName(file))))
                        {
                            notes.Add($"{loc.Path}: a recording with the same name is already there");
                        }
                        else if (projected.GetValueOrDefault(loc.Path) - bytes < show.KeepFreeGb * SpaceManager.Gb)
                        {
                            notes.Add($"{loc.Path}: no room (keeping {show.KeepFreeGb} GB free)");
                        }
                        else
                        {
                            projected[loc.Path] -= bytes;
                            showEntry.Destination = dest;
                            break;
                        }
                    }

                    if (showEntry.Destination is null)
                    {
                        showEntry.Why = show.Locations.Count == 0 ? "no locations listed for the show"
                            : "stays where it is: " + string.Join("; ", notes);
                    }
                }

                (showEntry.Destination is null ? plan.Skipped : plan.Items).Add(showEntry);
                continue;
            }

            var categories = Categories(item);
            var rule = plan.Rules.FirstOrDefault(r => r.Enabled && (!isSeries || r.IncludeSeries) && r.Matches(categories));
            if (rule is null)
            {
                // Series recordings no rule takes are where they belong; only the general folder's count.
                if (!isSeries)
                {
                    plan.Unmatched++;
                }

                continue;
            }

            var path = item.Path!;
            var entry = new SortItem
            {
                ItemId = item.Id,
                Name = item.Name,
                Path = path,
                Source = from!,
                Rule = rule.Name,
                Categories = string.Join(", ", categories)
            };

            entry.Why = rule.Problem is { } problem ? $"rule \"{rule.Name}\": {problem}" : Busy(path);
            if (entry.Why is not null)
            {
                plan.Skipped.Add(entry);
                continue;
            }

            entry.Destination = Path.GetFullPath(Path.Combine(rule.Destination, Path.GetRelativePath(from!, Path.GetDirectoryName(path)!)));
            if (File.Exists(Path.Combine(entry.Destination, Path.GetFileName(path))))
            {
                // A move would refuse to overwrite it; say so once instead of failing every run.
                entry.Why = "a recording with the same name is already at " + entry.Destination;
                plan.Skipped.Add(entry);
                continue;
            }

            plan.Items.Add(entry);
        }

        return plan;
    }

    /// <summary>Carries out the plan's moves; returns what happened to each.</summary>
    public List<SpaceManager.ActionRecord> Execute(SortPlan plan, CancellationToken ct)
    {
        var done = new List<SpaceManager.ActionRecord>();
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Destination is not { } dest
                || _libraryManager.GetItemById(item.ItemId) is not BaseItem current || current.Path != item.Path
                || _sessionManager.Sessions.Any(s => s.NowPlayingItem?.Id == item.ItemId)
                || _serviceProvider.GetService<IRecordingsManager>()?.GetActiveRecordingInfo(item.Path) is not null
                || IsOpen(item.Path))
            {
                continue; // changed since planning; the next run looks again
            }

            var record = new SpaceManager.ActionRecord
            {
                Time = DateTime.UtcNow,
                Name = item.Name,
                Action = "sort (" + item.Rule + ")",
                From = item.Path,
                To = dest,
                Bytes = RecordingFiles(item.Path).Sum(SafeLength)
            };
            try
            {
                MoveRecording(item.Path, item.Source, dest, _libraryMonitor, _logger);
                record.Ok = true;
                _logger.LogInformation("Sorted {Name} ({Rule}) to {Dest}", item.Name, item.Rule, dest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                record.Error = ex.Message;
                _logger.LogWarning(ex, "Could not sort {Path} to {Dest}", item.Path, dest);
            }

            done.Add(record);
        }

        return done;
    }

    private List<RuleStatus> CheckRules(PluginConfiguration cfg, LiveTvOptions tv, string source, string? series)
    {
        var libraryPaths = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Select(p => p.TrimEnd('/'))
            .ToList();
        var result = new List<RuleStatus>();
        foreach (var r in cfg.CategoryRules ?? Array.Empty<CategoryRule>())
        {
            var dest = Clean(r.Destination) ?? Clean(tv.MovieRecordingPath);
            var status = new RuleStatus
            {
                Name = string.IsNullOrWhiteSpace(r.Name) ? "(unnamed)" : r.Name.Trim(),
                Enabled = r.Enabled,
                RequireAll = r.RequireAll,
                IncludeSeries = r.IncludeSeries,
                Categories = (r.Categories ?? string.Empty)
                    .Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                Destination = dest ?? string.Empty
            };
            status.Problem = status.Categories.Count == 0 ? "no categories listed"
                : dest is null ? "no destination (and no movie recording folder in Live TV settings)"
                : dest == source || IsUnder(dest, source) ? "destination is inside the recording folder it sorts from"
                : r.IncludeSeries && series is not null && (dest == series || IsUnder(dest, series)) ? "destination is inside the series recording folder it sorts from"
                : !Directory.Exists(dest) ? "destination folder not found"
                : !libraryPaths.Any(l => dest == l || IsUnder(dest, l)) ? "destination is not inside any library (sorted recordings would disappear from Jellyfin)"
                : null;
            result.Add(status);
        }

        return result;
    }

    private List<ShowStatus> CheckShows(PluginConfiguration cfg, string source, string? series)
    {
        var libraryPaths = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Select(p => p.TrimEnd('/'))
            .ToList();
        var free = new Dictionary<string, (bool Completed, (long Free, long Total)? Value)>();
        var result = new List<ShowStatus>();
        foreach (var s in cfg.ShowLocations ?? Array.Empty<ShowLocation>())
        {
            if (string.IsNullOrWhiteSpace(s.Show))
            {
                continue;
            }

            var status = new ShowStatus { Show = s.Show.Trim(), Enabled = s.Enabled, KeepFreeGb = Math.Max(0, s.KeepFreeGb) };
            foreach (var path in (s.Locations ?? Array.Empty<string>()).Select(Clean).OfType<string>())
            {
                if (!free.TryGetValue(path, out var space))
                {
                    // Network shares can hang; only ask each one once a run.
                    space = s.Enabled ? SpaceManager.WithTimeout(() => Directory.Exists(path) ? SpaceManager.FreeSpace(path) : null) : (true, null);
                    free[path] = space;
                }

                status.Locations.Add(new LocationStatus
                {
                    Path = path,
                    FreeBytes = space.Value?.Free,
                    TotalBytes = space.Value?.Total,
                    Problem = path == source || IsUnder(path, source) || IsUnder(source, path) ? "overlaps the recording folder"
                        : series is not null && (path == series || IsUnder(path, series) || IsUnder(series, path)) ? "overlaps the series recording folder"
                        : !s.Enabled ? null
                        : space.Value is null ? (space.Completed ? "not found" : "not responding")
                        : !libraryPaths.Any(l => path == l || IsUnder(path, l)) ? "not inside any library (moved recordings would disappear from Jellyfin)"
                        : null
                });
            }

            result.Add(status);
        }

        return result;
    }

    /// <summary>The show a recording belongs to: its series name, or for one in the general folder its folder there.</summary>
    private static string? ShowOf(BaseItem item, string from)
    {
        if (item is Episode e && !string.IsNullOrWhiteSpace(e.SeriesName))
        {
            return e.SeriesName;
        }

        var rel = Path.GetRelativePath(from, Path.GetDirectoryName(item.Path!)!);
        return rel == "." ? item.Name : rel.Split('/')[0];
    }

    internal static bool SameShow(string a, string b) => Normal(a) == Normal(b);

    private static string Normal(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>
    /// The guide categories for a recording: the item's genres plus the &lt;genre&gt; entries in
    /// the guide .nfo Jellyfin's DVR writes beside it (kept even if metadata fetching later
    /// replaces the item's genres).
    /// </summary>
    internal static List<string> Categories(BaseItem item)
    {
        var set = new List<string>(item.Genres ?? Array.Empty<string>());
        var nfo = Path.ChangeExtension(item.Path!, ".nfo");
        if (File.Exists(nfo))
        {
            try
            {
                set.AddRange(XDocument.Load(nfo).Descendants("genre").Select(g => g.Value.Trim()));
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
            {
            }
        }

        return set.Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? Clean(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim().TrimEnd('/');

    public sealed class RuleStatus
    {
        public string Name { get; set; } = string.Empty;

        public bool Enabled { get; set; }

        public bool RequireAll { get; set; }

        public bool IncludeSeries { get; set; }

        public List<string> Categories { get; set; } = new();

        public string Destination { get; set; } = string.Empty;

        public string? Problem { get; set; }

        internal bool Matches(List<string> categories)
        {
            bool Has(string c) => categories.Contains(c, StringComparer.OrdinalIgnoreCase);
            return Categories.Count > 0 && (RequireAll ? Categories.All(Has) : Categories.Any(Has));
        }
    }

    public sealed class ShowStatus
    {
        public string Show { get; set; } = string.Empty;

        public bool Enabled { get; set; }

        public int KeepFreeGb { get; set; }

        public List<LocationStatus> Locations { get; set; } = new();
    }

    public sealed class LocationStatus
    {
        public string Path { get; set; } = string.Empty;

        public long? FreeBytes { get; set; }

        public long? TotalBytes { get; set; }

        public string? Problem { get; set; }
    }

    public sealed class SortItem
    {
        public Guid ItemId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        /// <summary>The recording folder it's sorted from (general or series).</summary>
        public string Source { get; set; } = string.Empty;

        public string Rule { get; set; } = string.Empty;

        public string Categories { get; set; } = string.Empty;

        public string? Destination { get; set; }

        public string? Why { get; set; }
    }

    public sealed class SortPlan
    {
        public DateTime CreatedUtc { get; set; }

        public string Mode { get; set; } = PluginConfiguration.SortPreview;

        public string Source { get; set; } = string.Empty;

        public string? Warning { get; set; }

        public List<RuleStatus> Rules { get; set; } = new();

        public List<ShowStatus> Shows { get; set; } = new();

        /// <summary>The library's show names, for picking one on the settings page.</summary>
        public List<string> KnownShows { get; set; } = new();

        public List<SortItem> Items { get; set; } = new();

        public List<SortItem> Skipped { get; set; } = new();

        /// <summary>Recordings in the folder that no enabled rule matches (they stay put).</summary>
        public int Unmatched { get; set; }
    }
}
