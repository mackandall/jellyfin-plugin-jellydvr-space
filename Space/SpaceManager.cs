using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DvrSpace.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSpace.Space;

/// <summary>
/// Keeps the DVR's recordings drive above a free-space floor. Every few minutes, and shortly
/// after a recording finishes, it works out which recordings to move to archive storage (or
/// delete) to get back above the floor, in the configured order, and — in active mode — does
/// it. Moves copy the recording with its .edl/.nfo/thumbnail into the same show/season layout
/// under the first archive folder with room, verify the copy, then remove the original, so
/// commercial markers, watched state and history carry over (Jellyfin re-links user data to
/// the moved item). Never touches recordings marked Keep, still recording, or being played.
/// </summary>
public sealed class SpaceManager : BackgroundService
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly TimeSpan FsTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AfterRecordingDelay = TimeSpan.FromMinutes(2);

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".mkv", ".mp4", ".m4v", ".m2ts", ".mpg", ".mpeg", ".avi", ".wtv", ".mov"
    };

    private readonly ILogger<SpaceManager> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ISessionManager _sessionManager;
    private readonly IConfigurationManager _config;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IServiceProvider _serviceProvider;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly ConcurrentQueue<ActionRecord> _recent = new();
    private Timer? _soon;

    public SpaceManager(
        ILogger<SpaceManager> logger,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ISessionManager sessionManager,
        IConfigurationManager config,
        ILibraryMonitor libraryMonitor,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _sessionManager = sessionManager;
        _config = config;
        _libraryMonitor = libraryMonitor;
        _serviceProvider = serviceProvider;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>The most recent plan (what would be / was done), for the settings page.</summary>
    public SpacePlan? LastPlan { get; private set; }

    public IReadOnlyList<ActionRecord> RecentActions => _recent.Reverse().ToList();

    // ---- scheduling ------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _soon = new Timer(_ => _ = RunSafeAsync("recording finished", stoppingToken), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            // Let the server finish starting before the first check.
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSafeAsync("periodic", stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(Config.CheckIntervalMinutes, 1, 1440)), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _libraryManager.ItemAdded -= OnItemAdded;
            _soon.Dispose();
        }
    }

    // A finished recording (a new video under the recordings folders): check shortly after,
    // once the library, Comskip and the others have seen it.
    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Video v && v.Path is { } p && ManagedRoots().Any(r => IsUnder(p, r)))
        {
            _soon?.Change(AfterRecordingDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RunSafeAsync(string reason, CancellationToken ct)
    {
        try
        {
            await RunAsync(reason, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Disk space check failed");
        }
    }

    /// <summary>
    /// Plans what to free on the recordings drive and, in active mode, does it. Returns the plan.
    /// </summary>
    public async Task<SpacePlan> RunAsync(string reason, CancellationToken ct)
    {
        await _runLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cfg = Config;
            var plan = await Task.Run(() => BuildPlan(cfg), ct).ConfigureAwait(false);
            plan.Reason = reason;
            LastPlan = plan;

            if (plan.NeededBytes <= 0)
            {
                _logger.LogDebug("Recordings drive has {Free:F0} GB free (keeping {Keep} GB); nothing to do", plan.FreeBytes / (double)Gb, cfg.KeepFreeGb);
                return plan;
            }

            _logger.LogInformation(
                "Recordings drive has {Free:F0} GB free, below the {Keep} GB floor ({Reason}); {Count} recording(s) planned{Mode}",
                plan.FreeBytes / (double)Gb, cfg.KeepFreeGb, reason, plan.Items.Count(i => i.Action != PlanItem.ActionNone),
                cfg.Mode == PluginConfiguration.ModeActive ? string.Empty : " (preview: nothing done)");
            if (plan.Warning is { } warning)
            {
                _logger.LogWarning("Disk space: {Warning}", warning);
            }

            if (cfg.Mode == PluginConfiguration.ModeActive)
            {
                await Task.Run(() => Execute(plan, cfg, ct), ct).ConfigureAwait(false);
            }

            return plan;
        }
        finally
        {
            _runLock.Release();
        }
    }

    // ---- planning --------------------------------------------------------------------------

    /// <summary>The DVR's recording folders (Live TV settings): the only places this manages.</summary>
    public IReadOnlyList<string> ManagedRoots()
    {
        var tv = _config.GetConfiguration<LiveTvOptions>("livetv");
        return new[] { tv.RecordingPath, tv.MovieRecordingPath, tv.SeriesRecordingPath }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.TrimEnd('/'))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private SpacePlan BuildPlan(PluginConfiguration cfg)
    {
        var roots = ManagedRoots();
        var plan = new SpacePlan { KeepFreeBytes = cfg.KeepFreeGb * Gb, Mode = cfg.Mode, CreatedUtc = DateTime.UtcNow };
        if (roots.Count == 0)
        {
            plan.Warning = "No recording folders are set in Live TV settings.";
            return plan;
        }

        var drive = FreeSpace(roots[0]);
        if (drive is null)
        {
            plan.Warning = $"Could not read free space for {roots[0]}.";
            return plan;
        }

        (plan.FreeBytes, plan.TotalBytes) = drive.Value;
        plan.NeededBytes = plan.KeepFreeBytes - plan.FreeBytes;
        plan.Targets = CheckTargets(cfg);
        if (plan.NeededBytes <= 0)
        {
            return plan;
        }

        var keep = new HashSet<string>(cfg.KeepItemIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var playing = _sessionManager.Sessions
            .Select(s => s.NowPlayingItem?.Id)
            .OfType<Guid>()
            .ToHashSet();
        var recordings = _serviceProvider.GetService<IRecordingsManager>();
        var users = _userManager.GetUsers().ToList();

        var candidates = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            })
            .Where(i => i.Path is { } p && roots.Any(r => IsUnder(p, r)))
            .Select(i => new Candidate(
                i,
                roots.First(r => IsUnder(i.Path!, r)),
                i is Movie ? "movies" : "tv",
                users.Any(u => _userDataManager.GetUserData(u, i)?.Played == true)))
            .ToList();

        IEnumerable<Candidate> ordered = cfg.Order switch
        {
            PluginConfiguration.OrderOldest => candidates.OrderBy(c => c.Item.DateCreated),
            PluginConfiguration.OrderMoviesFirst => candidates.OrderBy(c => c.Kind == "movies" ? 0 : 1).ThenBy(c => c.Item.DateCreated),
            _ => candidates.OrderBy(c => c.Watched ? 0 : 1).ThenBy(c => c.Item.DateCreated)
        };

        // Projected free space per archive target as items are assigned to it.
        var projected = plan.Targets.ToDictionary(t => t.Path, t => t.FreeBytes ?? 0);
        long freed = 0;
        foreach (var c in ordered)
        {
            if (freed >= plan.NeededBytes)
            {
                break;
            }

            var id = c.Item.Id.ToString("N");
            string? skip = keep.Contains(id) ? "marked Keep"
                : playing.Contains(c.Item.Id) ? "being played"
                : recordings?.GetActiveRecordingInfo(c.Item.Path!) is not null ? "still recording"
                : null;
            if (skip is not null)
            {
                plan.Skipped.Add(PlanItem.From(c, 0, PlanItem.ActionNone, null, skip));
                continue;
            }

            var files = RecordingFiles(c.Item.Path!);
            var size = files.Sum(f => SafeLength(f));
            var move = cfg.Action == PluginConfiguration.ActionMove
                || (cfg.Action == PluginConfiguration.ActionMoveUnwatchedDeleteWatched && !c.Watched);

            if (!move)
            {
                plan.Items.Add(PlanItem.From(c, size, PlanItem.ActionDelete, null,
                    cfg.Action == PluginConfiguration.ActionDelete ? "delete" : "watched: delete"));
                freed += size;
                continue;
            }

            var target = plan.Targets.FirstOrDefault(t =>
                t.Usable && (t.Kind == "both" || t.Kind == c.Kind)
                && projected[t.Path] - size >= t.KeepFreeGb * Gb);
            if (target is not null)
            {
                projected[target.Path] -= size;
                var dest = Path.Combine(target.Path, Path.GetRelativePath(c.Root, Path.GetDirectoryName(c.Item.Path!)!));
                plan.Items.Add(PlanItem.From(c, size, PlanItem.ActionMove, dest, "move to archive"));
                freed += size;
            }
            else if (cfg.WhenArchiveFull == PluginConfiguration.WhenFullDelete)
            {
                plan.Items.Add(PlanItem.From(c, size, PlanItem.ActionDelete, null, "archives full: delete"));
                freed += size;
            }
            else
            {
                plan.Warning = $"No archive location for {c.Kind} has room (or none is reachable); stopped with " +
                    $"{(plan.NeededBytes - freed) / (double)Gb:F0} GB still needed. Add or free an archive location, " +
                    "or choose \"delete\" when archives are full.";
                break;
            }
        }

        plan.PlannedBytes = freed;
        if (freed < plan.NeededBytes && plan.Warning is null)
        {
            plan.Warning = $"Only {freed / (double)Gb:F0} GB can be freed from recordings; {(plan.NeededBytes - freed) / (double)Gb:F0} GB short.";
        }

        return plan;
    }

    private List<TargetStatus> CheckTargets(PluginConfiguration cfg)
    {
        var libraryPaths = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Select(p => p.TrimEnd('/'))
            .ToList();
        var result = new List<TargetStatus>();
        foreach (var t in cfg.ArchiveTargets ?? Array.Empty<ArchiveTarget>())
        {
            var path = (t.Path ?? string.Empty).Trim().TrimEnd('/');
            if (path.Length == 0)
            {
                continue;
            }

            var status = new TargetStatus
            {
                Path = path,
                Kind = (t.Kind ?? "both").ToLowerInvariant(),
                KeepFreeGb = Math.Max(0, t.KeepFreeGb),
                InLibrary = libraryPaths.Any(l => path == l || IsUnder(path, l))
            };
            var free = WithTimeout(() => Directory.Exists(path) ? FreeSpace(path) : null);
            status.Reachable = free.Completed && free.Value is not null;
            status.FreeBytes = free.Value?.Free;
            status.Problem = !status.Reachable ? (free.Completed ? "not found" : "not responding")
                : !status.InLibrary ? "not inside any library (moved recordings would disappear from Jellyfin)"
                : ManagedRoots().Any(r => path == r || IsUnder(path, r) || IsUnder(r, path)) ? "overlaps the recordings folders"
                : null;
            result.Add(status);
        }

        return result;
    }

    // ---- doing it --------------------------------------------------------------------------

    private void Execute(SpacePlan plan, PluginConfiguration cfg, CancellationToken ct)
    {
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            var free = FreeSpace(ManagedRoots()[0]);
            if (free is not null && free.Value.Free >= plan.KeepFreeBytes)
            {
                break; // something else freed space meanwhile
            }

            if (_libraryManager.GetItemById(item.ItemId) is not BaseItem current || current.Path != item.Path)
            {
                continue; // gone or changed since planning
            }

            // Re-check the per-item guards right before acting.
            if ((cfg.KeepItemIds ?? Array.Empty<string>()).Contains(item.ItemId.ToString("N"), StringComparer.OrdinalIgnoreCase)
                || _sessionManager.Sessions.Any(s => s.NowPlayingItem?.Id == item.ItemId)
                || _serviceProvider.GetService<IRecordingsManager>()?.GetActiveRecordingInfo(item.Path) is not null)
            {
                continue;
            }

            var record = new ActionRecord { Time = DateTime.UtcNow, Name = item.Name, Action = item.Action, From = item.Path, To = item.Destination, Bytes = item.Bytes };
            try
            {
                if (item.Action == PlanItem.ActionMove && item.Destination is { } dest)
                {
                    MoveRecording(item.Path, item.Root, dest);
                }
                else if (item.Action == PlanItem.ActionDelete)
                {
                    DeleteRecording(current);
                }

                record.Ok = true;
                _logger.LogInformation("Disk space: {Action} {Name} ({Size:F1} GB){Dest}", item.Action, item.Name, item.Bytes / (double)Gb,
                    item.Destination is null ? string.Empty : " to " + item.Destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                record.Ok = false;
                record.Error = ex.Message;
                _logger.LogWarning(ex, "Disk space: could not {Action} {Path}", item.Action, item.Path);
            }

            _recent.Enqueue(record);
            while (_recent.Count > 200 && _recent.TryDequeue(out _))
            {
            }
        }
    }

    /// <summary>
    /// Moves one recording and its sidecars (.edl, .nfo, -thumb.jpg, …) into
    /// <paramref name="destDir"/>, copying the show/season .nfo and artwork there if missing.
    /// Copies to a temporary name, checks the size, then renames and removes the original, so
    /// a failure part-way never loses the recording. Leaves folders emptied of recordings tidy.
    /// </summary>
    private void MoveRecording(string videoPath, string root, string destDir)
    {
        var files = RecordingFiles(videoPath);
        var conflicts = files.Select(f => Path.Combine(destDir, Path.GetFileName(f))).Where(File.Exists).ToList();
        if (conflicts.Count > 0)
        {
            throw new IOException($"already exists at the destination: {Path.GetFileName(conflicts[0])}");
        }

        Directory.CreateDirectory(destDir);
        CopyFolderMetadata(Path.GetDirectoryName(videoPath)!, root, destDir);

        // The video last, so a half-copied move leaves no playable duplicate behind.
        var copied = new List<string>();
        try
        {
            foreach (var src in files.OrderBy(f => f == videoPath ? 1 : 0))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(src));
                var temp = dest + ".jdvrpart";
                File.Copy(src, temp, overwrite: true);
                if (new FileInfo(temp).Length != new FileInfo(src).Length)
                {
                    File.Delete(temp);
                    throw new IOException($"copy of {Path.GetFileName(src)} came out the wrong size");
                }

                File.Move(temp, dest);
                copied.Add(dest);
            }
        }
        catch
        {
            foreach (var c in copied)
            {
                TryDelete(c);
            }

            throw;
        }

        foreach (var src in files)
        {
            File.Delete(src);
        }

        var srcDir = Path.GetDirectoryName(videoPath)!;
        TidyEmptiedFolders(srcDir, root);
        _libraryMonitor.ReportFileSystemChanged(srcDir);
        _libraryMonitor.ReportFileSystemChanged(destDir);
    }

    private void DeleteRecording(BaseItem item)
    {
        var videoPath = item.Path!;
        var root = ManagedRoots().First(r => IsUnder(videoPath, r));
        var files = RecordingFiles(videoPath);
        _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
        foreach (var f in files.Where(File.Exists))
        {
            TryDelete(f); // sidecars Jellyfin leaves behind (.edl, Comskip's .txt, …)
        }

        TidyEmptiedFolders(Path.GetDirectoryName(videoPath)!, root);
    }

    // Show/season-level metadata (tvshow.nfo, season.nfo, folder.jpg…) that isn't another
    // recording's sidecar, copied to the destination only where missing.
    private static void CopyFolderMetadata(string srcDir, string root, string destDir)
    {
        var dir = srcDir;
        var dest = destDir;
        while (IsUnder(dir, root))
        {
            var videos = Directory.GetFiles(dir).Where(IsVideo).Select(Path.GetFileNameWithoutExtension).ToList();
            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (IsVideo(f) || videos.Any(v => BelongsTo(name, v!)) || File.Exists(Path.Combine(dest, name)))
                {
                    continue;
                }

                File.Copy(f, Path.Combine(dest, name));
            }

            dir = Path.GetDirectoryName(dir)!;
            dest = Path.GetDirectoryName(dest)!;
        }
    }

    // After the last recording leaves a season (and then show) folder, remove what's left of
    // it (only metadata) so the library doesn't keep an empty season or show.
    private void TidyEmptiedFolders(string dir, string root)
    {
        while (IsUnder(dir, root))
        {
            if (Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(IsVideo))
            {
                return;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not remove emptied folder {Dir}", dir);
                return;
            }

            dir = Path.GetDirectoryName(dir)!;
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>The recording plus its sidecars: same base name followed by "." or "-".</summary>
    private static List<string> RecordingFiles(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath)!;
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        return Directory.GetFiles(dir)
            .Where(f => f == videoPath || BelongsTo(Path.GetFileName(f), baseName))
            .Where(f => !f.EndsWith(".jdvrpart", StringComparison.Ordinal))
            .ToList();
    }

    private static bool BelongsTo(string fileName, string baseName) =>
        fileName.StartsWith(baseName + ".", StringComparison.Ordinal) || fileName.StartsWith(baseName + "-", StringComparison.Ordinal);

    private static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static (long Free, long Total)? FreeSpace(string path)
    {
        try
        {
            var d = new DriveInfo(path);
            return (d.AvailableFreeSpace, d.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Network shares can hang (hard mounts wait for the NAS); never let that stall a check.
    private static (bool Completed, T? Value) WithTimeout<T>(Func<T?> f)
    {
        var task = Task.Run(f);
        return task.Wait(FsTimeout) ? (true, task.Result) : (false, default);
    }

    internal sealed record Candidate(BaseItem Item, string Root, string Kind, bool Watched);

    public sealed class SpacePlan
    {
        public DateTime CreatedUtc { get; set; }

        public string Reason { get; set; } = string.Empty;

        public string Mode { get; set; } = PluginConfiguration.ModePreview;

        public long FreeBytes { get; set; }

        public long TotalBytes { get; set; }

        public long KeepFreeBytes { get; set; }

        public long NeededBytes { get; set; }

        public long PlannedBytes { get; set; }

        public string? Warning { get; set; }

        public List<TargetStatus> Targets { get; set; } = new();

        public List<PlanItem> Items { get; set; } = new();

        public List<PlanItem> Skipped { get; set; } = new();
    }

    public sealed class PlanItem
    {
        public const string ActionMove = "move";
        public const string ActionDelete = "delete";
        public const string ActionNone = "none";

        public Guid ItemId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string Root { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public bool Watched { get; set; }

        public DateTime Recorded { get; set; }

        public long Bytes { get; set; }

        public string Action { get; set; } = ActionNone;

        public string? Destination { get; set; }

        public string Why { get; set; } = string.Empty;

        internal static PlanItem From(Candidate c, long bytes, string action, string? destination, string why) => new()
        {
            ItemId = c.Item.Id,
            Name = c.Item is Episode e ? $"{e.SeriesName} S{e.ParentIndexNumber:00}E{e.IndexNumber:00} {e.Name}" : c.Item.Name,
            Path = c.Item.Path!,
            Root = c.Root,
            Kind = c.Kind,
            Watched = c.Watched,
            Recorded = c.Item.DateCreated,
            Bytes = bytes,
            Action = action,
            Destination = destination,
            Why = why
        };
    }

    public sealed class TargetStatus
    {
        public string Path { get; set; } = string.Empty;

        public string Kind { get; set; } = "both";

        public int KeepFreeGb { get; set; }

        public bool Reachable { get; set; }

        public bool InLibrary { get; set; }

        public long? FreeBytes { get; set; }

        public string? Problem { get; set; }

        public bool Usable => Reachable && Problem is null;
    }

    public sealed class ActionRecord
    {
        public DateTime Time { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string From { get; set; } = string.Empty;

        public string? To { get; set; }

        public long Bytes { get; set; }

        public bool Ok { get; set; }

        public string? Error { get; set; }
    }
}
