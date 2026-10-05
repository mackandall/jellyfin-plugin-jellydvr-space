using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSpace.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Jellyfin.Plugin.DvrSpace.Space.RecordingFileOps;

namespace Jellyfin.Plugin.DvrSpace.Space;

/// <summary>
/// "Move to archive…" from the web item menu: one recording, to an archive location the user
/// picks. Works in preview mode too (the click is the confirmation), uses the same safe move as
/// the space manager, and runs in the background since copying to a NAS takes a while.
/// </summary>
public sealed partial class SpaceManager
{
    // Recordings being moved right now (from the item menu or by the space manager), for progress.
    private readonly ConcurrentDictionary<Guid, MoveProgress> _moves = new();

    /// <summary>Recordings being moved right now, with how much has been copied so far.</summary>
    public IReadOnlyList<MoveProgress> Moving()
    {
        foreach (var m in _moves.Values)
        {
            // Each file is copied to "<name>.jdvrpart" and renamed when complete (see MoveRecording).
            m.CopiedBytes = m.FileNames.Sum(n =>
            {
                var dest = Path.Combine(m.Destination, n);
                return File.Exists(dest) ? SafeLength(dest) : File.Exists(dest + ".jdvrpart") ? SafeLength(dest + ".jdvrpart") : 0;
            });
        }

        return _moves.Values.OrderBy(m => m.QueuedUtc).ToList();
    }

    private MoveProgress? Track(BaseItem item, string destination)
    {
        var files = RecordingFiles(item.Path!);
        var progress = new MoveProgress
        {
            ItemId = item.Id,
            SeriesId = (item as Episode)?.SeriesId,
            Name = item is Episode e && !string.IsNullOrEmpty(e.SeriesName) ? $"{e.SeriesName}: {item.Name}" : item.Name,
            Destination = destination,
            Bytes = files.Sum(f => SafeLength(f)),
            FileNames = files.Select(Path.GetFileName).OfType<string>().ToList(),
            QueuedUtc = DateTime.UtcNow
        };
        return _moves.TryAdd(item.Id, progress) ? progress : null;
    }

    /// <summary>"sports" when the guide categories say so, else "movies" or "tv".</summary>
    internal static string KindOf(BaseItem item, PluginConfiguration cfg)
    {
        var sports = (cfg.SportsCategories ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sports.Length > 0 && item.Path is not null
            && CategorySorter.Categories(item).Any(c => sports.Contains(c, StringComparer.OrdinalIgnoreCase)))
        {
            return "sports";
        }

        return item is Movie ? "movies" : "tv";
    }

    /// <summary>Whether an archive location of <paramref name="targetKind"/> takes this recording.</summary>
    internal static bool Accepts(string targetKind, BaseItem item, string kind) =>
        targetKind == "both" || targetKind == kind || (kind == "sports" && targetKind == (item is Movie ? "movies" : "tv"));

    /// <summary>What the item menu shows: whether the item is a recording, and where it can go.</summary>
    public ArchiveChoices GetArchiveChoices(BaseItem item)
    {
        var cfg = Config;
        var root = item.Path is { } p ? ManagedRoots().FirstOrDefault(r => IsUnder(p, r)) : null;
        var choices = new ArchiveChoices { Name = item.Name, IsRecording = root is not null };
        if (root is null || item is not Video)
        {
            choices.IsRecording = false;
            return choices;
        }

        choices.Kind = KindOf(item, cfg);
        if (_moves.TryGetValue(item.Id, out var moving))
        {
            choices.InFlight = moving.Destination;
            choices.Progress = Moving().FirstOrDefault(m => m.ItemId == item.Id);
        }

        choices.Bytes = RecordingFiles(item.Path!).Sum(f => SafeLength(f));
        choices.Blocked = WhyNot(item);
        foreach (var t in CheckTargets(cfg)
                     .Where(t => Accepts(t.Kind, item, choices.Kind))
                     .OrderBy(t => t.Kind == choices.Kind ? 0 : t.Kind == "both" ? 2 : 1))
        {
            var after = (t.FreeBytes ?? 0) - choices.Bytes;
            choices.Targets.Add(new ArchiveChoice
            {
                Path = t.Path,
                Kind = t.Kind,
                FreeBytes = t.FreeBytes,
                TotalBytes = t.TotalBytes,
                KeepFreeGb = t.KeepFreeGb,
                Destination = Path.Combine(t.Path, Path.GetRelativePath(root, Path.GetDirectoryName(item.Path!)!)),
                Problem = t.Problem ?? (after < 0 ? "not enough free space" : null),
                Note = t.Problem is null && after >= 0 && after < t.KeepFreeGb * Gb
                    ? $"leaves it below its {t.KeepFreeGb} GB floor"
                    : null
            });
        }

        return choices;
    }

    /// <summary>Starts moving the recording to <paramref name="targetPath"/>; returns what's happening.</summary>
    public string StartArchive(BaseItem item, string targetPath)
    {
        var choices = GetArchiveChoices(item);
        if (!choices.IsRecording)
        {
            throw new InvalidOperationException("Only recordings in the DVR's recording folders can be archived.");
        }

        if (choices.InFlight is not null)
        {
            throw new InvalidOperationException("It's already being moved.");
        }

        if (choices.Blocked is { } why)
        {
            throw new InvalidOperationException($"It can't be moved while it's {why}.");
        }

        var choice = choices.Targets.FirstOrDefault(t => t.Path == targetPath.TrimEnd('/'))
            ?? throw new InvalidOperationException("That archive location doesn't take this recording.");
        if (choice.Problem is { } problem)
        {
            throw new InvalidOperationException($"{choice.Path}: {problem}.");
        }

        var progress = Track(item, choice.Destination)
            ?? throw new InvalidOperationException("It's already being moved.");

        var path = item.Path!;
        var root = ManagedRoots().First(r => IsUnder(path, r));
        _ = Task.Run(async () =>
        {
            var record = new ActionRecord
            {
                Time = DateTime.UtcNow, Name = item.Name, Action = "archive", From = path, To = choice.Destination, Bytes = choices.Bytes
            };
            await _runLock.WaitAsync().ConfigureAwait(false);
            progress.Started = true;
            try
            {
                // Checked again: the space manager may have moved it, or playback started, meanwhile.
                if (!File.Exists(path))
                {
                    throw new IOException("the recording is no longer there");
                }

                if (WhyNot(item) is { } now)
                {
                    throw new InvalidOperationException($"it's {now}");
                }

                MoveRecording(path, root, choice.Destination, _libraryMonitor, _logger);
                record.Ok = true;
                _logger.LogInformation("Archived {Name} ({Size:F1} GB) to {Dest}", item.Name, choices.Bytes / (double)Gb, choice.Destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                record.Error = ex.Message;
                _logger.LogWarning(ex, "Could not archive {Path}", path);
            }
            finally
            {
                _runLock.Release();
                _moves.TryRemove(item.Id, out _);
                Remember(record);
            }
        });

        return $"Moving \"{item.Name}\" ({choices.Bytes / (double)Gb:F1} GB) to {choice.Destination}. " +
            "It stays playable from where it is until the copy is checked; the library then picks it up in its new place.";
    }

    // Why the recording can't be moved right now, or null.
    private string? WhyNot(BaseItem item) =>
        _serviceProvider.GetService<IRecordingsManager>()?.GetActiveRecordingInfo(item.Path!) is not null ? "still recording"
        : _sessionManager.Sessions.Any(s => s.NowPlayingItem?.Id == item.Id) ? "being played"
        : IsOpen(item.Path!) ? "in use by another program (a commercial scan?)"
        : null;

    public sealed class ArchiveChoices
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>False: not a DVR recording, so the menu entry isn't shown.</summary>
        public bool IsRecording { get; set; }

        /// <summary>"tv", "movies" or "sports".</summary>
        public string Kind { get; set; } = string.Empty;

        public long Bytes { get; set; }

        /// <summary>Why it can't be moved right now (e.g. "still recording"), or null.</summary>
        public string? Blocked { get; set; }

        /// <summary>Where it's being moved to right now, or null.</summary>
        public string? InFlight { get; set; }

        public List<ArchiveChoice> Targets { get; set; } = new();

        /// <summary>The move in progress, or null.</summary>
        public MoveProgress? Progress { get; set; }
    }

    public sealed class MoveProgress
    {
        public Guid ItemId { get; set; }

        /// <summary>The show, for an episode: so a show's card can say one of its recordings is moving.</summary>
        public Guid? SeriesId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Destination { get; set; } = string.Empty;

        public long Bytes { get; set; }

        public long CopiedBytes { get; set; }

        /// <summary>False while it waits for another move or a space check to finish.</summary>
        public bool Started { get; set; }

        public DateTime QueuedUtc { get; set; }

        internal List<string> FileNames { get; set; } = new();
    }

    public sealed class ArchiveChoice
    {
        public string Path { get; set; } = string.Empty;

        public string Kind { get; set; } = "both";

        public long? FreeBytes { get; set; }

        public long? TotalBytes { get; set; }

        /// <summary>The location's own free-space floor (GB).</summary>
        public int KeepFreeGb { get; set; }

        /// <summary>The folder the recording would end up in (show/season layout kept).</summary>
        public string Destination { get; set; } = string.Empty;

        /// <summary>Why it can't go there, or null.</summary>
        public string? Problem { get; set; }

        /// <summary>A warning that doesn't stop the move, or null.</summary>
        public string? Note { get; set; }
    }
}
