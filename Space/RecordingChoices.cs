using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.DvrSpace.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Jellyfin.Plugin.DvrSpace.Space.RecordingFileOps;

namespace Jellyfin.Plugin.DvrSpace.Space;

/// <summary>
/// "Store this recording in…": one recording's own folders, which win over its show's. Picked for a
/// guide programme, the choice waits for Jellyfin to start recording it and then notes the file;
/// picked for a recording already made, it's tied to the file at once. Gone once the file is.
/// </summary>
public sealed partial class SpaceManager
{
    // A choice for a programme that never got recorded is dropped this long after it ended.
    private static readonly TimeSpan UnrecordedKept = TimeSpan.FromHours(6);

    private static readonly object ChoicesLock = new();

    /// <summary>What a recording's own choice is, for the picker; null when the item can't have one.</summary>
    internal RecordingStorage? GetRecordingStorage(BaseItem item, PluginConfiguration cfg, List<StorageFolder> folders)
    {
        var target = Describe(item);
        if (target is null)
        {
            return null;
        }

        var choice = Find(cfg.RecordingChoices ?? Array.Empty<RecordingChoice>(), target);
        var result = new RecordingStorage
        {
            Title = target.Title,
            Upcoming = item is LiveTvProgram,
            Mode = choice is null ? "show" : choice.StayOnDrive ? "drive" : "folders",
            Locations = choice?.Locations.ToList() ?? new List<string>()
        };
        if (result.Mode == "folders")
        {
            result.MovesTo = FirstWithRoom(result.Locations, folders);
        }

        return result;
    }

    /// <summary>Saves one recording's choice: "show" (use the show's setting), "drive" (stay put) or "folders".</summary>
    public string SetRecordingStorage(BaseItem item, string mode, IReadOnlyList<string> locations)
    {
        if (Plugin.Instance is not { } plugin)
        {
            throw new InvalidOperationException("JellyDVR Space isn't loaded.");
        }

        var target = Describe(item) ?? throw new InvalidOperationException("Only a guide programme or a recording still on the recording drive can have its own folder.");
        var cfg = plugin.Configuration;
        var known = (cfg.ArchiveTargets ?? Array.Empty<ArchiveTarget>())
            .Select(t => (t.Path ?? string.Empty).Trim().TrimEnd('/'))
            .ToHashSet();
        var paths = locations.Select(p => p.Trim().TrimEnd('/')).Where(p => p.Length > 0).Distinct().ToList();
        if (paths.FirstOrDefault(p => !known.Contains(p)) is { } unknown)
        {
            throw new InvalidOperationException($"{unknown} isn't one of the storage folders.");
        }

        if (mode == "folders" && paths.Count == 0)
        {
            mode = "show";
        }

        var hadFiles = target.Path is not null;
        lock (ChoicesLock)
        {
            var list = (cfg.RecordingChoices ?? Array.Empty<RecordingChoice>()).ToList();
            var old = Find(list, target);
            hadFiles |= old?.Paths.Length > 0;
            if (old is not null)
            {
                list.Remove(old);
            }

            if (mode is "drive" or "folders")
            {
                list.Add(new RecordingChoice
                {
                    Title = target.Title,
                    ProgramId = target.ProgramId,
                    ChannelId = target.ChannelId,
                    StartUtc = target.StartUtc,
                    EndUtc = target.EndUtc,
                    // Chosen again in the guide while it records: keep the files already noted.
                    Paths = target.Path is { } p ? new[] { p } : old?.Paths ?? Array.Empty<string>(),
                    Locations = mode == "folders" ? paths.ToArray() : Array.Empty<string>(),
                    StayOnDrive = mode == "drive",
                    CreatedUtc = DateTime.UtcNow
                });
            }

            cfg.RecordingChoices = list.ToArray();
            plugin.SaveConfiguration();
        }

        _logger.LogInformation(
            "Storage for {Title} set to {Choice}",
            target.Title,
            mode == "folders" ? string.Join(", ", paths) : mode == "drive" ? "the recording drive" : "the show's setting");

        // A recording already made can move now.
        if (hadFiles)
        {
            _soon?.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        }

        var said = mode switch
        {
            "folders" => $"This recording: stored in {string.Join(", then ", paths.Select(FolderLabel))}.",
            "drive" => "This recording stays on the recording drive.",
            _ => "This recording is back to the default (the show's setting)."
        };
        return mode != "folders" || cfg.SortMode == PluginConfiguration.SortActive
            ? said
            : said + " (Sorting is in " + (cfg.SortMode == PluginConfiguration.SortOff ? "Off" : "Preview") + " on the server, so nothing moves yet.)";
    }

    /// <summary>
    /// Ties choices made in the guide to the files Jellyfin is recording them to, and forgets
    /// choices whose recording is gone (moved, deleted, or never made).
    /// </summary>
    internal void NoteRecordingFiles()
    {
        if (Plugin.Instance is not { } plugin || (plugin.Configuration.RecordingChoices ?? Array.Empty<RecordingChoice>()).Length == 0)
        {
            return;
        }

        var active = ActiveTimers();
        lock (ChoicesLock)
        {
            var cfg = plugin.Configuration;
            var changed = false;
            var list = new List<RecordingChoice>();
            foreach (var c in cfg.RecordingChoices ?? Array.Empty<RecordingChoice>())
            {
                foreach (var t in active.Where(t => Matches(c, t)))
                {
                    if (!c.Paths.Contains(t.Path))
                    {
                        c.Paths = c.Paths.Append(t.Path).ToArray();
                        changed = true;
                        _logger.LogInformation("Recording {Title} is going to {Path}; it has its own storage choice", c.Title, t.Path);
                    }
                }

                var recording = c.Paths.Any(p => active.Any(t => t.Recording && t.Path == p));
                var kept = c.Paths.Length > 0
                    ? recording || c.Paths.Any(File.Exists)
                    : DateTime.UtcNow < c.EndUtc + UnrecordedKept;
                if (kept)
                {
                    list.Add(c);
                }
                else
                {
                    changed = true;
                    _logger.LogInformation("Forgot the storage choice for {Title} ({Why})", c.Title, c.Paths.Length > 0 ? "its file is gone" : "it wasn't recorded");
                }
            }

            if (changed)
            {
                cfg.RecordingChoices = list.ToArray();
                plugin.SaveConfiguration();
            }
        }
    }

    private static bool Matches(RecordingChoice c, ActiveTimer t) =>
        c.ChannelId.Length > 0 && string.Equals(c.ChannelId, t.ChannelId, StringComparison.OrdinalIgnoreCase)
        && ((c.ProgramId.Length > 0 && c.ProgramId == t.ProgramId) || Math.Abs((c.StartUtc - t.StartUtc).TotalMinutes) < 1);

    private static RecordingChoice? Find(IEnumerable<RecordingChoice> choices, Target target) =>
        target.Path is { } path
            ? choices.FirstOrDefault(c => c.Paths.Contains(path))
            : choices.FirstOrDefault(c => c.ChannelId == target.ChannelId
                && (c.ProgramId == target.ProgramId || c.StartUtc == target.StartUtc));

    private static string? FirstWithRoom(IEnumerable<string> locations, List<StorageFolder> folders) =>
        locations.Select(p => folders.FirstOrDefault(f => f.Path == p))
            .FirstOrDefault(f => f is { Problem: null, FreeBytes: { } free } && free >= f.KeepFreeGb * Gb)?.Label;

    /// <summary>A guide programme, or a recording still on the recording drive; null for anything else.</summary>
    private Target? Describe(BaseItem item)
    {
        if (item is LiveTvProgram p)
        {
            var channel = _libraryManager.GetItemById(p.ChannelId);
            if (channel?.ExternalId is not { Length: > 0 } channelId)
            {
                return null;
            }

            var title = string.IsNullOrWhiteSpace(p.EpisodeTitle) ? p.Name : p.Name + ": " + p.EpisodeTitle;
            var start = p.StartDate;
            return new Target(
                $"{title}, {start.ToLocalTime().ToString("MMM d h:mm tt", CultureInfo.InvariantCulture)} on {channel.Name}",
                p.ExternalId ?? string.Empty,
                channelId,
                start,
                p.EndDate ?? start.AddHours(1),
                null);
        }

        if (item.Path is { } path && ManagedRoots().Any(r => IsUnder(path, r)))
        {
            var title = item is Episode e && !string.IsNullOrEmpty(e.SeriesName) ? e.SeriesName + ": " + item.Name : item.Name;
            return new Target(title, string.Empty, string.Empty, DateTime.MinValue, DateTime.MinValue, path);
        }

        return null;
    }

    /// <summary>The timers Jellyfin is recording right now or finished in the last day, with their files, from its own timer store (fast; see Schedule's ActiveRecordings).</summary>
    private List<ActiveTimer> ActiveTimers()
    {
        var result = new List<ActiveTimer>();
        var recordings = _serviceProvider.GetService<IRecordingsManager>();
        try
        {
            var file = Path.Combine(_config.CommonApplicationPaths.DataPath, "livetv", "timers.json");
            if (recordings is null || !File.Exists(file))
            {
                return result;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var start = t.TryGetProperty("StartDate", out var s) && s.TryGetDateTime(out var d) ? d.ToUniversalTime() : DateTime.MinValue;
                var status = Str(t, "Status");
                // Recording now; or finished lately (missed while it ran, e.g. over a restart): Jellyfin noted its file.
                var path = status is "InProgress" or "2" && Str(t, "Id") is { } id ? recordings.GetActiveRecordingPath(id)
                    : status is "Completed" or "3" && DateTime.UtcNow - start < TimeSpan.FromDays(1) && Str(t, "RecordingPath") is { Length: > 0 } done && File.Exists(done) ? done
                    : null;
                if (path is null)
                {
                    continue;
                }

                result.Add(new ActiveTimer(path, Str(t, "ProgramId") ?? string.Empty, Str(t, "ChannelId") ?? string.Empty, start, status is "InProgress" or "2"));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Can't read Jellyfin's timers.json");
        }

        return result;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    private sealed record Target(string Title, string ProgramId, string ChannelId, DateTime StartUtc, DateTime EndUtc, string? Path);

    private sealed record ActiveTimer(string Path, string ProgramId, string ChannelId, DateTime StartUtc, bool Recording);

    public sealed class RecordingStorage
    {
        /// <summary>The programme or recording, e.g. "Forensic Files: Bitter Pill, Oct 26 9:00 PM on 25.1 KOKH".</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>A guide programme (the choice waits for the recording) rather than a recording.</summary>
        public bool Upcoming { get; set; }

        /// <summary>"show" (uses the show's setting), "drive" (stays on the recording drive) or "folders".</summary>
        public string Mode { get; set; } = "show";

        public List<string> Locations { get; set; } = new();

        /// <summary>With "folders": the folder it would go to now, or null.</summary>
        public string? MovesTo { get; set; }
    }
}
