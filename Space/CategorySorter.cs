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
/// rule a recording matches moves it (with its sidecars, same safe copy-then-remove as archive
/// moves). Categories come from the item's genres and the guide .nfo Jellyfin writes beside it.
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
        var source = Clean(tv.RecordingPath);
        if (source is null)
        {
            plan.Warning = "No recording path is set in Live TV settings.";
            return plan;
        }

        plan.Source = source;
        // Movie and series recordings already have their own folders; leave them alone even
        // if those folders sit inside the general one.
        var elsewhere = new[] { Clean(tv.MovieRecordingPath), Clean(tv.SeriesRecordingPath) }
            .OfType<string>()
            .Where(p => p != source)
            .ToList();
        plan.Rules = CheckRules(cfg, tv, source);

        var keep = new HashSet<string>(cfg.KeepItemIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var playing = _sessionManager.Sessions.Select(s => s.NowPlayingItem?.Id).OfType<Guid>().ToHashSet();
        var recordings = _serviceProvider.GetService<IRecordingsManager>();

        var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Video },
                IsVirtualItem = false,
                Recursive = true
            })
            .Where(i => i.Path is { } p && IsUnder(p, source) && !elsewhere.Any(e => IsUnder(p, e)))
            .GroupBy(i => i.Path) // the same file can appear in more than one library
            .Select(g => g.First())
            .OrderBy(i => i.DateCreated);

        foreach (var item in items.Where(i => !SpaceManager.IsLiveBuffer(i))) // buffers stay put until kept
        {
            var categories = Categories(item);
            var rule = plan.Rules.FirstOrDefault(r => r.Enabled && r.Matches(categories));
            if (rule is null)
            {
                plan.Unmatched++;
                continue;
            }

            var path = item.Path!;
            var entry = new SortItem
            {
                ItemId = item.Id,
                Name = item.Name,
                Path = path,
                Rule = rule.Name,
                Categories = string.Join(", ", categories)
            };

            entry.Why = rule.Problem is { } problem ? $"rule \"{rule.Name}\": {problem}"
                : keep.Contains(item.Id.ToString("N")) ? "marked Keep"
                : playing.Contains(item.Id) ? "being played"
                : recordings?.GetActiveRecordingInfo(path) is not null ? "still recording"
                : IsOpen(path) ? "in use (e.g. commercial detection still reading it); will retry"
                : null;
            if (entry.Why is not null)
            {
                plan.Skipped.Add(entry);
                continue;
            }

            entry.Destination = Path.GetFullPath(Path.Combine(rule.Destination, Path.GetRelativePath(source, Path.GetDirectoryName(path)!)));
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
                MoveRecording(item.Path, plan.Source, dest, _libraryMonitor, _logger);
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

    private List<RuleStatus> CheckRules(PluginConfiguration cfg, LiveTvOptions tv, string source)
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
                Categories = (r.Categories ?? string.Empty)
                    .Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                Destination = dest ?? string.Empty
            };
            status.Problem = status.Categories.Count == 0 ? "no categories listed"
                : dest is null ? "no destination (and no movie recording folder in Live TV settings)"
                : dest == source || IsUnder(dest, source) ? "destination is inside the recording folder it sorts from"
                : !Directory.Exists(dest) ? "destination folder not found"
                : !libraryPaths.Any(l => dest == l || IsUnder(dest, l)) ? "destination is not inside any library (sorted recordings would disappear from Jellyfin)"
                : null;
            result.Add(status);
        }

        return result;
    }

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

        public List<string> Categories { get; set; } = new();

        public string Destination { get; set; } = string.Empty;

        public string? Problem { get; set; }

        internal bool Matches(List<string> categories)
        {
            bool Has(string c) => categories.Contains(c, StringComparer.OrdinalIgnoreCase);
            return Categories.Count > 0 && (RequireAll ? Categories.All(Has) : Categories.Any(Has));
        }
    }

    public sealed class SortItem
    {
        public Guid ItemId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

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

        public List<SortItem> Items { get; set; } = new();

        public List<SortItem> Skipped { get; set; } = new();

        /// <summary>Recordings in the folder that no enabled rule matches (they stay put).</summary>
        public int Unmatched { get; set; }
    }
}
