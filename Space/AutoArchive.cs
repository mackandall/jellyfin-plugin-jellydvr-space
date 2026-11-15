using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
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
/// "Archive automatically": finished recordings that a rule takes move from the recording folders to
/// storage once they are processed (scanned for commercials, joined, not waiting to be recorded again)
/// and the rule's conditions are met (watched, days old). Off unless turned on; in preview it only
/// lists what it would move. Runs with the space check, before it, one move at a time.
/// </summary>
public sealed partial class SpaceManager
{
    // Moves that failed (a name already taken at the destination, …), by file: shown, not retried
    // until the server restarts or the recording changes path.
    private readonly ConcurrentDictionary<string, string> _autoArchiveFailed = new(StringComparer.Ordinal);

    /// <summary>The last "Archive automatically" plan, for the settings page.</summary>
    public AutoArchivePlan? LastAutoArchive { get; private set; }

    private void AutoArchive(PluginConfiguration cfg, string reason, CancellationToken ct)
    {
        var mode = (cfg.AutoArchiveMode ?? PluginConfiguration.SortOff).ToLowerInvariant();
        if (mode == PluginConfiguration.SortOff)
        {
            LastAutoArchive = new AutoArchivePlan { Mode = mode, CreatedUtc = DateTime.UtcNow };
            return;
        }

        var plan = BuildAutoArchivePlan(cfg, mode);
        LastAutoArchive = plan;
        if (plan.Items.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Archive automatically ({Reason}): {Count} recording(s) ready{Mode}", reason, plan.Items.Count,
            mode == PluginConfiguration.SortActive ? string.Empty : " (preview: nothing done)");
        if (mode != PluginConfiguration.SortActive)
        {
            return;
        }

        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (_libraryManager.GetItemById(item.ItemId) is not BaseItem current || current.Path != item.Path || item.Destination is not { } dest)
            {
                continue;
            }

            // Re-check the guards right before moving: planning can be minutes old by now.
            if ((cfg.KeepItemIds ?? Array.Empty<string>()).Contains(item.ItemId.ToString("N"), StringComparer.OrdinalIgnoreCase)
                || _sessionManager.Sessions.Any(s => s.NowPlayingItem?.Id == item.ItemId)
                || _serviceProvider.GetService<IRecordingsManager>()?.GetActiveRecordingInfo(item.Path) is not null)
            {
                continue;
            }

            var record = new ActionRecord
            {
                Time = DateTime.UtcNow, Name = item.Name, Action = "archive (" + item.Rule + ")", From = item.Path, To = dest, Bytes = item.Bytes
            };
            var progress = Track(current, dest);
            if (progress is null)
            {
                continue; // already being moved from the item menu
            }

            try
            {
                progress.Started = true;
                MoveRecording(item.Path, item.Root, dest, _libraryMonitor, _logger);
                record.Ok = true;
                _logger.LogInformation("Archive automatically: moved {Name} ({Size:F1} GB) to {Dest} (rule {Rule})",
                    item.Name, item.Bytes / (double)Gb, dest, item.Rule);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                record.Ok = false;
                record.Error = ex.Message;
                _autoArchiveFailed[item.Path] = ex.Message;
                _logger.LogWarning(ex, "Archive automatically: could not move {Path}", item.Path);
            }
            finally
            {
                _moves.TryRemove(current.Id, out _);
            }

            Remember(record);
        }

        // Look again so the page shows what's left rather than what was just moved.
        LastAutoArchive = BuildAutoArchivePlan(cfg, mode);
    }

    private AutoArchivePlan BuildAutoArchivePlan(PluginConfiguration cfg, string mode)
    {
        var plan = new AutoArchivePlan { Mode = mode, CreatedUtc = DateTime.UtcNow };
        var rules = (cfg.AutoArchiveRules ?? Array.Empty<AutoArchiveRule>()).Where(r => r.Enabled).ToList();
        var roots = ManagedRoots();
        if (rules.Count == 0 || roots.Count == 0)
        {
            plan.Warning = rules.Count == 0 ? "No rule is on." : "No recording folders are set in Live TV settings.";
            return plan;
        }

        var targets = CheckTargets(cfg);
        if (!targets.Any(t => t.Usable))
        {
            plan.Warning = targets.Count == 0
                ? "There are no storage folders yet (section 3)."
                : "No storage folder is reachable: " + string.Join("; ", targets.Select(t => t.Path + " (" + t.Problem + ")"));
        }

        var keep = new HashSet<string>(cfg.KeepItemIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var playing = _sessionManager.Sessions.Select(s => s.NowPlayingItem?.Id).OfType<Guid>().ToHashSet();
        var recordings = _serviceProvider.GetService<IRecordingsManager>();
        var airing = AiringsInProgress.Read(_config);
        var scans = ComskipScans.Read(_config);
        var rerecords = cfg.AutoArchiveRerecordWaitDays > 0 ? PendingRerecords.Read(_config, cfg.AutoArchiveRerecordWaitDays) : PendingRerecords.None;
        var users = _userManager.GetUsers().ToList();
        // "Everyone": the accounts that can sign in (a disabled one would hold recordings forever).
        var everyone = users.Where(u => !u.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled)).ToList();
        var own = (cfg.RecordingChoices ?? Array.Empty<RecordingChoice>())
            .SelectMany(c => c.Paths ?? Array.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);
        var shows = (cfg.ShowLocations ?? Array.Empty<ShowLocation>())
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Show) && (s.Locations ?? Array.Empty<string>()).Length > 0)
            .Select(s => s.Show)
            .ToList();
        var perRun = Math.Max(1, cfg.AutoArchivePerRun);
        var now = DateTime.UtcNow;

        var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.Video },
                IsVirtualItem = false,
                Recursive = true
            })
            .Where(i => i.Path is { } p && roots.Any(r => IsUnder(p, r)) && File.Exists(p))
            .GroupBy(i => i.Path, StringComparer.Ordinal) // the same file can be in more than one library
            .Select(g => g.First())
            .OrderBy(i => i.DateCreated)
            .ToList();

        var projected = targets.ToDictionary(t => t.Path, t => t.FreeBytes ?? 0);
        foreach (var item in items)
        {
            if (IsLiveBuffer(item))
            {
                continue; // deleted by the space manager or kept by JellyDVR Live, never archived
            }

            var kind = KindOf(item, cfg);
            var rule = rules.FirstOrDefault(r => Matches(r, item, kind));
            if (rule is null)
            {
                continue;
            }

            plan.Matched++;
            var name = DisplayName(item);
            var ruleName = string.IsNullOrWhiteSpace(rule.Name) ? Describe(rule) : rule.Name;
            var path = item.Path!;
            if (_autoArchiveFailed.TryGetValue(path, out var failed))
            {
                plan.Failed.Add(new AutoArchiveItem { ItemId = item.Id, Name = name, Rule = ruleName, Why = failed, Path = path });
                continue;
            }

            var age = now - item.DateCreated;
            var watched = rule.Watched switch
            {
                AutoArchiveRule.WatchedSomeone => users.Any(u => _userDataManager.GetUserData(u, item)?.Played == true),
                AutoArchiveRule.WatchedEveryone => everyone.Count > 0 && everyone.All(u => _userDataManager.GetUserData(u, item)?.Played == true),
                _ => true
            };
            var scan = scans.Why(item, cfg.AutoArchiveScanWaitHours, now);
            string? wait = keep.Contains(item.Id.ToString("N")) ? "marked Keep"
                : recordings?.GetActiveRecordingInfo(path) is not null ? "still recording"
                : airing.Why(item) is { } a ? a
                : own.Contains(path) ? "has its own storage choice"
                : item is not Movie && SpaceManager.ShowNameOf(item) is { } show && shows.Any(s => CategorySorter.SameShow(s, show)) ? "its show has storage folders (section 4)"
                : rerecords.Contains(item) ? "incomplete: waiting to be recorded again"
                : scan is not null ? scan
                : !watched ? (rule.Watched == AutoArchiveRule.WatchedEveryone ? "not watched by everyone yet" : "not watched yet")
                : age.TotalDays < rule.AfterDays ? $"{Math.Max(0, (int)age.TotalDays)} of {rule.AfterDays} days"
                : playing.Contains(item.Id) ? "being played"
                : _moves.ContainsKey(item.Id) ? "being moved"
                : null;
            if (wait is not null)
            {
                plan.Waiting.Add(new AutoArchiveItem { ItemId = item.Id, Name = name, Rule = ruleName, Why = wait, Path = path });
                continue;
            }

            var root = roots.First(r => IsUnder(path, r));
            var size = RecordingFiles(path).Sum(f => SafeLength(f));
            var wanted = (rule.Locations ?? Array.Empty<string>()).Select(p => p.Trim().TrimEnd('/')).Where(p => p.Length > 0).ToList();
            var candidates = wanted.Count > 0
                ? wanted.Select(p => targets.FirstOrDefault(t => t.Path == p)).OfType<TargetStatus>()
                : targets.Where(t => Accepts(t.Kind, item, kind)).OrderBy(t => t.Kind == kind ? 0 : 1);
            var target = candidates.FirstOrDefault(t => t.Usable && projected[t.Path] - size >= t.KeepFreeGb * Gb);
            if (target is null)
            {
                plan.Waiting.Add(new AutoArchiveItem
                {
                    ItemId = item.Id, Name = name, Rule = ruleName, Path = path,
                    Why = wanted.Count > 0 ? "the rule's storage folders have no room" : $"no storage folder for {kind} has room"
                });
                continue;
            }

            if (plan.Items.Count >= perRun)
            {
                plan.Waiting.Add(new AutoArchiveItem { ItemId = item.Id, Name = name, Rule = ruleName, Path = path, Why = $"next check ({perRun} per check)" });
                continue;
            }

            projected[target.Path] -= size;
            plan.Items.Add(new AutoArchiveItem
            {
                ItemId = item.Id,
                Name = name,
                Rule = ruleName,
                Path = path,
                Root = root,
                Bytes = size,
                Destination = Path.Combine(target.Path, Path.GetRelativePath(root, Path.GetDirectoryName(path)!))
            });
        }

        return plan;
    }

    internal static bool Matches(AutoArchiveRule rule, BaseItem item, string kind)
    {
        var match = (rule.Match ?? string.Empty).Trim();
        return (rule.Which ?? AutoArchiveRule.WhichTv).ToLowerInvariant() switch
        {
            AutoArchiveRule.WhichAll => true,
            AutoArchiveRule.WhichMovies => item is Movie,
            AutoArchiveRule.WhichSports => kind == "sports",
            AutoArchiveRule.WhichCategory => match.Length > 0 && CategorySorter.Categories(item).Any(c =>
                match.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(c, StringComparer.OrdinalIgnoreCase)),
            AutoArchiveRule.WhichShow => match.Length > 0 && ShowNameOf(item) is { } show && item is not Movie && CategorySorter.SameShow(show, match),
            _ => item is not Movie
        };
    }

    private static string Describe(AutoArchiveRule r) => (r.Which ?? "tv") switch
    {
        AutoArchiveRule.WhichCategory or AutoArchiveRule.WhichShow => r.Match,
        var w => w
    };

    private static string DisplayName(BaseItem item) => item is Episode e
        ? $"{e.SeriesName} S{e.ParentIndexNumber:00}E{e.IndexNumber:00} {e.Name}"
        : item.Name;

    public sealed class AutoArchivePlan
    {
        public DateTime CreatedUtc { get; set; }

        public string Mode { get; set; } = PluginConfiguration.SortOff;

        /// <summary>Recordings some rule takes (ready, waiting or failed).</summary>
        public int Matched { get; set; }

        public string? Warning { get; set; }

        /// <summary>Ready: moved now (active) or would be (preview).</summary>
        public List<AutoArchiveItem> Items { get; set; } = new();

        public List<AutoArchiveItem> Waiting { get; set; } = new();

        /// <summary>Moves that failed (a name already taken, …): not retried.</summary>
        public List<AutoArchiveItem> Failed { get; set; } = new();
    }

    public sealed class AutoArchiveItem
    {
        public Guid ItemId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Rule { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string Root { get; set; } = string.Empty;

        public long Bytes { get; set; }

        public string? Destination { get; set; }

        public string? Why { get; set; }
    }
}

/// <summary>
/// What the Comskip plugin has done with each recording, from its own files (no dependency on it):
/// a scan result (breaks, none, failed, or marks from an .edl), or still queued.
/// </summary>
internal sealed class ComskipScans
{
    private readonly bool _installed;
    private readonly HashSet<Guid> _done;
    private readonly HashSet<Guid> _queued;

    private ComskipScans(bool installed, HashSet<Guid> done, HashSet<Guid> queued)
    {
        _installed = installed;
        _done = done;
        _queued = queued;
    }

    public static ComskipScans Read(MediaBrowser.Common.Configuration.IConfigurationManager config)
    {
        var folder = Path.Combine(config.CommonApplicationPaths.DataPath, "comskip-segments");
        var done = new HashSet<Guid>();
        var queued = new HashSet<Guid>();
        if (!Directory.Exists(folder))
        {
            return new ComskipScans(false, done, queued);
        }

        try
        {
            var state = Path.Combine(folder, "detection-state.json");
            if (File.Exists(state))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(state));
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (Guid.TryParse(p.Name, out var id))
                    {
                        done.Add(id);
                    }
                }
            }

            var queue = Path.Combine(folder, "scan-queue.json");
            if (File.Exists(queue))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(queue));
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (e.TryGetProperty("Id", out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var id))
                    {
                        queued.Add(id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
        }

        return new ComskipScans(true, done, queued);
    }

    /// <summary>Why the recording should wait for its scan, or null.</summary>
    public string? Why(BaseItem item, int waitHours, DateTime now)
    {
        if (!_installed || waitHours <= 0)
        {
            return null;
        }

        if (_queued.Contains(item.Id))
        {
            return "commercial scan queued";
        }

        if (_done.Contains(item.Id))
        {
            return null;
        }

        var waited = now - item.DateCreated;
        return waited.TotalHours < waitHours ? $"waiting for its commercial scan ({(int)waited.TotalHours} of {waitHours} h)" : null;
    }
}

/// <summary>
/// Recordings Episode History will record again (incomplete, an airing found or being looked for),
/// from its programmes.json: they wait so the new copy can replace them where they are.
/// </summary>
internal sealed class PendingRerecords
{
    public static readonly PendingRerecords None = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<Guid>());

    private readonly HashSet<string> _paths;
    private readonly HashSet<Guid> _ids;

    private PendingRerecords(HashSet<string> paths, HashSet<Guid> ids)
    {
        _paths = paths;
        _ids = ids;
    }

    public static PendingRerecords Read(MediaBrowser.Common.Configuration.IConfigurationManager config, int waitDays)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<Guid>();
        try
        {
            var file = Path.Combine(config.CommonApplicationPaths.DataPath, "episode-history", "programmes.json");
            if (!File.Exists(file))
            {
                return None;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return None;
            }

            var cutoff = DateTime.UtcNow.AddDays(-waitDays);
            foreach (var p in doc.RootElement.EnumerateObject().Select(x => x.Value))
            {
                var state = p.TryGetProperty("State", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                if (state is not ("waiting" or "scheduled"))
                {
                    continue;
                }

                var since = Date(p, "FlaggedUtc") ?? Date(p, "ProgramEndUtc") ?? DateTime.MinValue;
                if (since < cutoff)
                {
                    continue;
                }

                if (p.TryGetProperty("JoinedPath", out var j) && j.ValueKind == JsonValueKind.String && j.GetString() is { Length: > 0 } joined)
                {
                    paths.Add(joined);
                }

                if (p.TryGetProperty("Pieces", out var pieces) && pieces.ValueKind == JsonValueKind.Array)
                {
                    foreach (var piece in pieces.EnumerateArray())
                    {
                        if (piece.TryGetProperty("Path", out var pp) && pp.ValueKind == JsonValueKind.String && pp.GetString() is { Length: > 0 } path)
                        {
                            paths.Add(path);
                        }
                    }
                }

                if (p.TryGetProperty("ItemId", out var i) && i.ValueKind == JsonValueKind.String && Guid.TryParse(i.GetString(), out var id))
                {
                    ids.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
        }

        return new PendingRerecords(paths, ids);
    }

    public bool Contains(BaseItem item) => _ids.Contains(item.Id) || (item.Path is { } p && _paths.Contains(p));

    private static DateTime? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var d) ? d.ToUniversalTime() : null;
}
