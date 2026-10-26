using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.DvrSpace.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using static Jellyfin.Plugin.DvrSpace.Space.RecordingFileOps;

namespace Jellyfin.Plugin.DvrSpace.Space;

/// <summary>
/// "Store this show in…" for clients (JDVR): one show's storage folders, read and saved by any
/// item of the show (the series, an episode, a guide programme), so a TV never types a path.
/// </summary>
public sealed partial class SpaceManager
{
    public ShowStorage GetShowStorage(BaseItem item)
    {
        var cfg = Config;
        var show = ShowNameOf(item);
        var result = new ShowStorage { Show = show ?? string.Empty, SortMode = cfg.SortMode };
        if (show is null)
        {
            result.Problem = "Movies aren't stored by show.";
            return result;
        }

        var entry = (cfg.ShowLocations ?? Array.Empty<ShowLocation>())
            .FirstOrDefault(s => s.Enabled && CategorySorter.SameShow(s.Show ?? string.Empty, show));
        result.Locations = (entry?.Locations ?? Array.Empty<string>())
            .Select(p => p.Trim().TrimEnd('/'))
            .Where(p => p.Length > 0)
            .ToList();
        // Movie-only folders aren't offered for a show, unless it already uses one (to take it out).
        result.Folders = CheckTargets(cfg)
            .Where(t => t.Kind != "movies" || result.Locations.Contains(t.Path))
            .Select(t => new StorageFolder
            {
                Path = t.Path,
                Label = FolderLabel(t.Path),
                Kind = t.Kind,
                KeepFreeGb = t.KeepFreeGb,
                FreeBytes = t.FreeBytes,
                TotalBytes = t.TotalBytes,
                Problem = t.Problem
            })
            .ToList();
        result.MovesTo = result.Locations
            .Select(p => result.Folders.FirstOrDefault(f => f.Path == p))
            .FirstOrDefault(f => f is { Problem: null, FreeBytes: { } free } && free >= f.KeepFreeGb * Gb)?.Label;
        result.OnRecordingDrive = item.Path is { } path && ManagedRoots().Any(r => IsUnder(path, r));
        return result;
    }

    /// <summary>Saves a show's folders (empty: it stays on the recording drive); returns what to tell the viewer.</summary>
    public string SetShowStorage(string show, IReadOnlyList<string> locations)
    {
        if (Plugin.Instance is not { } plugin)
        {
            throw new InvalidOperationException("JellyDVR Space isn't loaded.");
        }

        show = show.Trim();
        if (show.Length == 0)
        {
            throw new InvalidOperationException("No show given.");
        }

        var cfg = plugin.Configuration;
        var known = (cfg.ArchiveTargets ?? Array.Empty<ArchiveTarget>())
            .Select(t => (t.Path ?? string.Empty).Trim().TrimEnd('/'))
            .ToHashSet();
        var paths = locations.Select(p => p.Trim().TrimEnd('/')).Where(p => p.Length > 0).Distinct().ToList();
        if (paths.FirstOrDefault(p => !known.Contains(p)) is { } unknown)
        {
            throw new InvalidOperationException($"{unknown} isn't one of the storage folders.");
        }

        var list = (cfg.ShowLocations ?? Array.Empty<ShowLocation>())
            .Where(s => !CategorySorter.SameShow(s.Show ?? string.Empty, show))
            .ToList();
        if (paths.Count > 0)
        {
            list.Add(new ShowLocation { Show = show, Enabled = true, Locations = paths.ToArray() });
        }

        cfg.ShowLocations = list.ToArray();
        plugin.SaveConfiguration();
        _logger.LogInformation("Storage for {Show} set to {Locations}", show, paths.Count > 0 ? string.Join(", ", paths) : "the recording drive");

        // Look soon, so recordings already on the drive start moving.
        _soon?.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        var said = paths.Count > 0
            ? $"{show}: stored in {string.Join(", then ", paths.Select(FolderLabel))}."
            : $"{show} stays on the recording drive.";
        return cfg.SortMode == PluginConfiguration.SortActive || paths.Count == 0
            ? said
            : said + " (Sorting is in " + (cfg.SortMode == PluginConfiguration.SortOff ? "Off" : "Preview") + " on the server, so nothing moves yet.)";
    }

    /// <summary>The show an item belongs to; null for movies.</summary>
    internal static string? ShowNameOf(BaseItem item) => item switch
    {
        Series s => s.Name,
        Season s => s.SeriesName,
        Episode e => e.SeriesName,
        LiveTvProgram p when p.IsMovie => null,
        LiveTvProgram p => p.Name,
        MediaBrowser.Controller.Entities.Movies.Movie => null,
        _ => string.IsNullOrWhiteSpace(item.Name) ? null : item.Name
    };

    /// <summary>A short name for a folder: its share (the folder under /mnt or /media) and its own name, e.g. "dvr_storage / TV".</summary>
    internal static string FolderLabel(string path)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && (parts[0] == "mnt" || parts[0] == "media"))
        {
            return parts[1] + " / " + parts[^1];
        }

        return parts.Length >= 2 ? parts[^2] + " / " + parts[^1] : path;
    }

    public sealed class ShowStorage
    {
        public string Show { get; set; } = string.Empty;

        /// <summary>The chosen folders, in order (empty: stays on the recording drive).</summary>
        public List<string> Locations { get; set; } = new();

        /// <summary>Every storage folder, to choose from.</summary>
        public List<StorageFolder> Folders { get; set; } = new();

        /// <summary>The folder the next finished recording would go to, or null.</summary>
        public string? MovesTo { get; set; }

        /// <summary>The item asked about is a recording still on the recording drive.</summary>
        public bool OnRecordingDrive { get; set; }

        /// <summary>Sorting's mode on the server: moves happen only when it's "active".</summary>
        public string SortMode { get; set; } = PluginConfiguration.SortPreview;

        /// <summary>Why this item can't have one (a movie), or null.</summary>
        public string? Problem { get; set; }
    }

    public sealed class StorageFolder
    {
        public string Path { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public string Kind { get; set; } = "both";

        public int KeepFreeGb { get; set; }

        public long? FreeBytes { get; set; }

        public long? TotalBytes { get; set; }

        public string? Problem { get; set; }
    }
}
