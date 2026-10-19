using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DvrSpace.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSpace.Space;

/// <summary>What the page shows for one movie, and what the converter keeps about it.</summary>
public sealed class ConvertEntry
{
    public const string Todo = "todo";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Broken = "broken";
    public const string Skipped = "skipped";

    public string Status { get; set; } = Todo;

    /// <summary>"ok", "warn" (a gap) or "bad" (ends early, unreadable); null until checked.</summary>
    public string? Health { get; set; }

    public string? HealthText { get; set; }

    public string? Message { get; set; }

    public long OriginalBytes { get; set; }

    public long NewBytes { get; set; }

    public string? BackupPath { get; set; }

    public DateTime? UndoUntilUtc { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A movie on the page: the library's facts plus the converter's entry.</summary>
public sealed record ConvertRow(
    Guid Id,
    string Name,
    int? Year,
    string Path,
    string Where,
    long Bytes,
    double Minutes,
    double Mbps,
    long AfterBytes,
    DateTime Recorded,
    ConvertEntry Entry);

/// <summary>The movie being converted now.</summary>
public sealed record ConvertJob(Guid Id, string Name, string Where, double Minutes, DateTime StartedUtc)
{
    public double Done { get; set; }

    public double Speed { get; set; }

    public string Phase { get; set; } = "checking";

    /// <summary>Time spent paused (a recording or Live TV had the Arc), left out of the speed.</summary>
    public double PausedSeconds { get; set; }
}

/// <summary>
/// Converts the heavy H.264 movie captures (1080p, before the Arc card) to HEVC on the Arc, one at a
/// time, from a queue the Convert page fills. Each movie is first checked for gaps (a recording that
/// lost signal; converting it would only shrink a broken copy), then encoded beside itself as a
/// hidden file, verified (same length within 1.5 s, same audio tracks) and swapped in under its own
/// name; the original stays hidden for <see cref="PluginConfiguration.ConvertKeepDays"/> for Undo.
/// Runs only in the configured hours and never while a tuner is recording.
/// </summary>
public sealed class Converter : BackgroundService
{
    private const string HiddenSuffix = ".h264-original";
    private const string WorkSuffix = ".hevc-converting";
    private static readonly string[] Extensions = { ".ts", ".mpg", ".m2ts" };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly ILogger<Converter> _logger;
    private readonly ILibraryManager _library;
    private readonly ILiveTvManager _liveTv;
    private readonly ISessionManager _sessions;
    private readonly IMediaEncoder _encoder;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly string _storePath;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _wake = new(0);
    private Dictionary<Guid, ConvertEntry> _entries = new();
    private CancellationTokenSource? _jobCancel;
    private List<ConvertRow>? _rowsCache;
    private DateTime _rowsAt;

    public Converter(
        ILogger<Converter> logger,
        ILibraryManager library,
        ILiveTvManager liveTv,
        ISessionManager sessions,
        IMediaEncoder encoder,
        IProviderManager providers,
        IFileSystem fileSystem,
        IApplicationPaths paths)
    {
        _logger = logger;
        _library = library;
        _liveTv = liveTv;
        _sessions = sessions;
        _encoder = encoder;
        _providers = providers;
        _fileSystem = fileSystem;
        var folder = System.IO.Path.Combine(paths.PluginConfigurationsPath, "JellyDvrSpace");
        Directory.CreateDirectory(folder);
        _storePath = System.IO.Path.Combine(folder, "convert.json");
        Load();
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>The movie being converted now, if any.</summary>
    public ConvertJob? Current { get; private set; }

    /// <summary>Why the queue isn't moving, when it isn't ("outside 1–7 AM", "a tuner is recording").</summary>
    public string? Waiting { get; private set; }

    // ---- the list ---------------------------------------------------------------------------

    /// <summary>
    /// The movies worth converting: H.264 at 1080p or less, in a broadcast container, at least the
    /// configured bitrate; plus any the converter already has an entry for (done, broken…).
    /// </summary>
    public IReadOnlyList<ConvertRow> Rows(bool fresh = false)
    {
        lock (_lock)
        {
            if (!fresh && _rowsCache is not null && DateTime.UtcNow - _rowsAt < TimeSpan.FromMinutes(10))
            {
                return _rowsCache.Select(r => r with { Entry = EntryFor(r.Id) }).ToList();
            }
        }

        var cfg = Config;
        var minMbps = Math.Max(1, cfg.ConvertMinMbps);
        var rows = new List<ConvertRow>();
        var items = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie }, Recursive = true, IsVirtualItem = false });
        foreach (var item in items.OfType<Video>())
        {
            var path = item.Path;
            if (string.IsNullOrEmpty(path) || !Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()))
            {
                continue;
            }

            Guid id = item.Id;
            ConvertEntry? known;
            lock (_lock)
            {
                _entries.TryGetValue(id, out known);
            }

            var video = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var minutes = (item.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerMinute;
            long bytes;
            try
            {
                bytes = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                continue;
            }

            var mbps = minutes > 0 ? bytes * 8 / (minutes * 60) / 1e6 : 0;
            var candidate = video is not null
                && string.Equals(video.Codec, "h264", StringComparison.OrdinalIgnoreCase)
                && (video.Width ?? 0) <= 1920
                && mbps >= minMbps
                && minutes >= 20;
            if (!candidate && known is null)
            {
                continue;
            }

            var after = (long)(minutes * 60 * (Math.Clamp(cfg.ConvertMbps, 3, 30) + 0.4) * 1e6 / 8);
            rows.Add(new ConvertRow(
                id, item.Name, item.ProductionYear, path, Where(path), bytes, Math.Round(minutes, 1), Math.Round(mbps, 1), after,
                item.DateCreated, known ?? new ConvertEntry()));
        }

        lock (_lock)
        {
            _rowsCache = rows;
            _rowsAt = DateTime.UtcNow;
        }

        return rows;
    }

    private ConvertEntry EntryFor(Guid id)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(id, out var e) ? e : new ConvertEntry();
        }
    }

    // "USBShareD", "NetworkUSB_storage": the share or drive the file is on (its mount name).
    private static string Where(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0] == "mnt")
        {
            return parts[1];
        }

        return parts.FirstOrDefault() ?? path;
    }

    // ---- actions from the page ----------------------------------------------------------------

    public void Queue(IEnumerable<Guid> ids)
    {
        lock (_lock)
        {
            foreach (var id in ids)
            {
                var e = Get(id);
                if (e.Status is ConvertEntry.Todo or ConvertEntry.Failed or ConvertEntry.Broken or ConvertEntry.Skipped)
                {
                    e.Status = ConvertEntry.Queued;
                    e.Message = null;
                    e.UpdatedUtc = DateTime.UtcNow;
                }
            }

            Save();
        }

        _wake.Release();
    }

    public void SetStatus(Guid id, string status)
    {
        lock (_lock)
        {
            var e = Get(id);
            if (e.Status is ConvertEntry.Running or ConvertEntry.Done)
            {
                return;
            }

            e.Status = status;
            e.UpdatedUtc = DateTime.UtcNow;
            Save();
        }
    }

    /// <summary>Stops the movie being converted; it goes back on the list untouched.</summary>
    public void Stop()
    {
        _jobCancel?.Cancel();
    }

    /// <summary>Gap checks for movies not converted yet, in the background (about a minute each).</summary>
    public void Check(IEnumerable<Guid> ids)
    {
        var list = ids.ToList();
        _ = Task.Run(async () =>
        {
            foreach (var id in list)
            {
                if (_library.GetItemById(id) is not { Path: { } path })
                {
                    continue;
                }

                try
                {
                    var (health, text) = await CheckGapsAsync(path, CancellationToken.None).ConfigureAwait(false);
                    lock (_lock)
                    {
                        var e = Get(id);
                        e.Health = health;
                        e.HealthText = text;
                        e.UpdatedUtc = DateTime.UtcNow;
                        Save();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gap check failed for {Path}", path);
                }
            }
        });
    }

    /// <summary>Puts the original back in place of the converted file.</summary>
    public string Undo(Guid id)
    {
        if (_library.GetItemById(id) is not { Path: { } path } item)
        {
            return "No such movie.";
        }

        ConvertEntry e;
        lock (_lock)
        {
            e = Get(id);
        }

        if (e.Status != ConvertEntry.Done || e.BackupPath is null || !File.Exists(e.BackupPath))
        {
            return "There's no kept original to put back.";
        }

        try
        {
            File.Move(e.BackupPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't put the original back: {ex.Message}";
        }

        lock (_lock)
        {
            e.Status = ConvertEntry.Skipped;
            e.Message = "Original put back";
            e.BackupPath = null;
            e.UndoUntilUtc = null;
            e.NewBytes = 0;
            e.UpdatedUtc = DateTime.UtcNow;
            Save();
        }

        Refresh(item);
        _logger.LogInformation("Convert: put the original of {Path} back", path);
        return "The original is back.";
    }

    private ConvertEntry Get(Guid id)
    {
        if (!_entries.TryGetValue(id, out var e))
        {
            e = new ConvertEntry();
            _entries[id] = e;
        }

        return e;
    }

    // ---- the worker -------------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A movie left "running" by a restart goes back in the queue, and its half-written hidden
        // copy is removed now (it would stay if the movie were then taken off the queue).
        List<Guid> interrupted;
        lock (_lock)
        {
            interrupted = _entries.Where(kv => kv.Value.Status == ConvertEntry.Running).Select(kv => kv.Key).ToList();
            foreach (var id in interrupted)
            {
                _entries[id].Status = ConvertEntry.Queued;
            }

            Save();
        }

        foreach (var id in interrupted)
        {
            if (_library.GetItemById(id) is { Path: { } p })
            {
                TryDelete(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(p)!, "." + System.IO.Path.GetFileNameWithoutExtension(p) + WorkSuffix + System.IO.Path.GetExtension(p)));
            }
        }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                ExpireOriginals();
                var next = await NextAsync(stoppingToken).ConfigureAwait(false);
                if (next is null)
                {
                    await _wake.WaitAsync(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ConvertAsync(next.Value, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<Guid?> NextAsync(CancellationToken ct)
    {
        Guid? id;
        lock (_lock)
        {
            id = _entries.Where(kv => kv.Value.Status == ConvertEntry.Queued).OrderBy(kv => kv.Value.UpdatedUtc).Select(kv => (Guid?)kv.Key).FirstOrDefault();
        }

        if (id is null)
        {
            Waiting = null;
            return null;
        }

        var cfg = Config;
        if (!InWindow(cfg, DateTime.Now))
        {
            Waiting = $"Waiting for {Hour(cfg.ConvertFromHour)}–{Hour(cfg.ConvertToHour)}";
            return null;
        }

        if (cfg.ConvertPauseWhileRecording && await RecordingAsync(ct).ConfigureAwait(false))
        {
            Waiting = "Waiting: a tuner is recording";
            return null;
        }

        if (_sessions.Sessions.Any(s => s.NowPlayingItem?.Type == BaseItemKind.TvChannel))
        {
            Waiting = "Waiting: Live TV is being watched";
            return null;
        }

        if (CaptureRunning())
        {
            Waiting = "Waiting: a BroadcastHub capture is using the Arc";
            return null;
        }

        if (_sessions.Sessions.Any(s => s.NowPlayingItem?.Id == id))
        {
            Waiting = "Waiting: the next movie is being watched";
            return null;
        }

        Waiting = null;
        return id;
    }

    private static bool InWindow(PluginConfiguration cfg, DateTime now)
    {
        int from = cfg.ConvertFromHour, to = cfg.ConvertToHour;
        if (from < 0 || to < 0 || from == to)
        {
            return true;
        }

        var h = now.Hour;
        return from < to ? h >= from && h < to : h >= from || h < to;
    }

    private static string Hour(int h) => DateTime.Today.AddHours(Math.Clamp(h, 0, 23)).ToString("h tt", CultureInfo.InvariantCulture);

    private const int SigStop = 19;
    private const int SigCont = 18;

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int sig);

    private bool Signal(int pid, int sig)
    {
        try
        {
            return SysKill(pid, sig) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "Couldn't signal ffmpeg");
            return false;
        }
    }

    // A recording in progress (when that setting is on), someone watching a Live TV channel, or a BroadcastHub
    // capture encoding on the Arc (also those Channels DVR starts, which Jellyfin doesn't know about).
    private async Task<bool> ArcNeededAsync(CancellationToken ct) =>
        _sessions.Sessions.Any(s => s.NowPlayingItem?.Type == BaseItemKind.TvChannel)
        || CaptureRunning()
        || (Config.ConvertPauseWhileRecording && await RecordingAsync(ct).ConfigureAwait(false));

    private static bool CaptureRunning()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!char.IsDigit(Path.GetFileName(dir)[0]))
                {
                    continue;
                }

                try
                {
                    if (File.ReadAllText(Path.Combine(dir, "comm")).Trim() == "magewell2ts")
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                    // The process ended while we looked.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private async Task<bool> RecordingAsync(CancellationToken ct)
    {
        try
        {
            var timers = await _liveTv.GetTimers(new TimerQuery { IsActive = true }, ct).ConfigureAwait(false);
            return timers.Items.Any(t => t.Status == RecordingStatus.InProgress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Couldn't read the recording timers");
            return false;
        }
    }

    private async Task ConvertAsync(Guid id, CancellationToken stoppingToken)
    {
        if (_library.GetItemById(id) is not Video { Path: { } path } item || !File.Exists(path))
        {
            Finish(id, ConvertEntry.Failed, "The file isn't there any more.");
            return;
        }

        var cfg = Config;
        var minutes = (item.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerMinute;
        var job = new ConvertJob(id, item.Name, Where(path), Math.Round(minutes, 1), DateTime.UtcNow);
        Current = job;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _jobCancel = cancel;
        var ct = cancel.Token;
        lock (_lock)
        {
            var e = Get(id);
            e.Status = ConvertEntry.Running;
            e.Message = null;
            e.OriginalBytes = new FileInfo(path).Length;
            Save();
        }

        var folder = System.IO.Path.GetDirectoryName(path)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        var work = System.IO.Path.Combine(folder, "." + name + WorkSuffix + ext);
        var backup = System.IO.Path.Combine(folder, "." + name + HiddenSuffix + ext);
        try
        {
            TryDelete(work);

            // 1. Is this a whole recording? A broken one is better re-recorded than converted.
            var (health, text) = await CheckGapsAsync(path, ct).ConfigureAwait(false);
            lock (_lock)
            {
                var e = Get(id);
                e.Health = health;
                e.HealthText = text;
                Save();
            }

            if (health != "ok")
            {
                Finish(id, ConvertEntry.Broken, text);
                return;
            }

            // 2. Room for the new copy beside it (about half the size), with 2 GB to spare.
            var need = (long)(minutes * 60 * (cfg.ConvertMbps + 2) * 1e6 / 8) + (2L << 30);
            var free = Free(folder);
            if (free >= 0 && free < need)
            {
                Finish(id, ConvertEntry.Failed, $"Not enough room on its drive ({free / 1073741824.0:F1} GB free, needs {need / 1073741824.0:F1} GB).");
                return;
            }

            // 3. Encode beside it, as a hidden file.
            job.Phase = "converting";
            var (ok, why) = await EncodeAsync(path, work, minutes, cfg, job, ct).ConfigureAwait(false);
            if (!ok)
            {
                TryDelete(work);
                Finish(id, why == "stopped" ? ConvertEntry.Todo : ConvertEntry.Failed, why == "stopped" ? "Stopped; the movie is untouched." : why);
                return;
            }

            // 4. The same movie? Length within 1.5 s, the same audio tracks.
            job.Phase = "verifying";
            var (inLen, inAudio) = await ProbeAsync(path, ct).ConfigureAwait(false);
            var (outLen, outAudio) = await ProbeAsync(work, ct).ConfigureAwait(false);
            if (outLen <= 0 || Math.Abs(outLen - inLen) > 1.5 || outAudio != inAudio)
            {
                TryDelete(work);
                Finish(id, ConvertEntry.Failed, $"The new copy didn't match (length {outLen - inLen:+0.0;-0.0} s, {outAudio} of {inAudio} audio tracks); the original is untouched.");
                return;
            }

            // 5. Swap: the original goes hidden (kept for Undo), the new copy takes its name.
            job.Phase = "finishing";
            TryDelete(backup);
            File.Move(path, backup);
            try
            {
                File.Move(work, path);
            }
            catch
            {
                File.Move(backup, path, overwrite: true);
                throw;
            }

            var keep = Math.Max(0, cfg.ConvertKeepDays);
            lock (_lock)
            {
                var e = Get(id);
                e.Status = ConvertEntry.Done;
                e.NewBytes = new FileInfo(path).Length;
                e.BackupPath = keep > 0 ? backup : null;
                e.UndoUntilUtc = keep > 0 ? DateTime.UtcNow.AddDays(keep) : null;
                e.Message = null;
                e.UpdatedUtc = DateTime.UtcNow;
                Save();
            }

            if (keep == 0)
            {
                TryDelete(backup);
            }

            Refresh(item);
            _logger.LogInformation("Convert: {Path} is now HEVC ({Before:N0} → {After:N0} MB)", path, EntryFor(id).OriginalBytes >> 20, EntryFor(id).NewBytes >> 20);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            TryDelete(work);
            Finish(id, ConvertEntry.Todo, "Stopped; the movie is untouched.");
        }
        catch (OperationCanceledException)
        {
            TryDelete(work);
            lock (_lock)
            {
                Get(id).Status = ConvertEntry.Queued;
                Save();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Convert failed for {Path}", path);
            TryDelete(work);
            Finish(id, ConvertEntry.Failed, ex.Message);
        }
        finally
        {
            Current = null;
            _jobCancel = null;
            lock (_lock)
            {
                _rowsCache = null;
            }
        }
    }

    private void Finish(Guid id, string status, string? message)
    {
        lock (_lock)
        {
            var e = Get(id);
            e.Status = status;
            e.Message = message;
            e.UpdatedUtc = DateTime.UtcNow;
            Save();
        }

        _logger.LogInformation("Convert: {Id} {Status}: {Message}", id, status, message);
    }

    private void Refresh(BaseItem item)
    {
        try
        {
            _providers.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)) { ReplaceAllMetadata = false, MetadataRefreshMode = MetadataRefreshMode.Default }, RefreshPriority.High);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Refresh after convert failed");
        }
    }

    private void ExpireOriginals()
    {
        List<(Guid Id, string Path)> due;
        lock (_lock)
        {
            due = _entries.Where(kv => kv.Value.BackupPath is not null && kv.Value.UndoUntilUtc < DateTime.UtcNow)
                .Select(kv => (kv.Key, kv.Value.BackupPath!)).ToList();
        }

        foreach (var (id, p) in due)
        {
            TryDelete(p);
            lock (_lock)
            {
                var e = Get(id);
                e.BackupPath = null;
                e.UndoUntilUtc = null;
                Save();
            }

            _logger.LogInformation("Convert: deleted the kept original {Path}", p);
        }
    }

    // ---- ffmpeg / ffprobe ---------------------------------------------------------------------------

    private async Task<(bool Ok, string Why)> EncodeAsync(string input, string output, double minutes, PluginConfiguration cfg, ConvertJob job, CancellationToken ct)
    {
        var mbps = Math.Clamp(cfg.ConvertMbps, 3, 30);
        var device = string.IsNullOrWhiteSpace(cfg.ConvertDevice) ? "/dev/dri/renderD129" : cfg.ConvertDevice.Trim();
        // Some captures (most of the Emby archive's) open with one video packet stamped 2^33, outside
        // MPEG-TS's 33-bit clock. ffmpeg then starts the copy ~25 h late, its clock wraps partway and the
        // copy can't be measured (it fails the check below although complete). Half a second in, it's gone.
        var skip = await BrokenFirstVideoPtsAsync(input, ct).ConfigureAwait(false);
        if (skip)
        {
            _logger.LogInformation("Convert: {Path} opens with a broken video timestamp; starting 0.5 s in", input);
        }

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            // child_device picks the GPU; "qsv=hw:<path>" is ignored and lands on the first Intel GPU (the UHD 770).
            "-init_hw_device", "qsv=hw,child_device=" + device, "-filter_hw_device", "hw",
            "-hwaccel", "qsv", "-hwaccel_output_format", "qsv", "-c:v", "h264_qsv",
            // Some recordings open with seconds of stream that carry no picture size or sample rate yet
            // (Red Dawn 2012, Weapons, A Southern Haunting); with the default look-ahead ffmpeg starts
            // without them and fails at once. Normal files are read no further than before.
            "-probesize", "200M", "-analyzeduration", "120M",
        };
        if (skip)
        {
            args.AddRange(new[] { "-ss", "0.5" });
        }

        args.AddRange(new[]
        {
            "-i", input,
            "-map", "0:v:0", "-map", "0:a?",
            "-c:v", "hevc_qsv", "-preset", "slow",
            "-b:v", mbps + "M", "-maxrate", (int)Math.Round(mbps * 1.55) + "M", "-bufsize", mbps * 2 + "M",
            "-g", "120", "-look_ahead_depth", "40", "-extbrc", "1",
            // Captures repeat timestamps now and then: keep every frame as it came.
            "-fps_mode", "passthrough",
            "-c:a", "copy",
            "-f", "mpegts",
            "-progress", "pipe:1", "-nostats",
            output
        });
        var psi = new ProcessStartInfo { FileName = _encoder.EncoderPath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = new Process { StartInfo = psi };
        var errors = new ConcurrentQueue<string>();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } d)
            {
                errors.Enqueue(d);
                while (errors.Count > 30)
                {
                    errors.TryDequeue(out string? _);
                }
            }
        };
        var total = Math.Max(1, minutes * 60);
        var started = DateTime.UtcNow;
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), out var us) && us > 0 && us / 1e6 < total * 1.25)
            {
                var s = us / 1e6;
                job.Done = Math.Clamp(s / total, 0, 1);
                var elapsed = (DateTime.UtcNow - started).TotalSeconds - job.PausedSeconds;
                job.Speed = elapsed > 5 ? s / elapsed : 0;
            }
        };
        p.Start();
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        var paused = false;
        try
        {
            // BroadcastHub encodes live channels on the same Arc: while a recording runs or Live TV is
            // watched, the conversion is paused (SIGSTOP) and picks up where it was afterwards.
            while (true)
            {
                var exit = p.WaitForExitAsync(ct);
                var pausedAt = DateTime.UtcNow;
                if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(15), ct)).ConfigureAwait(false) == exit)
                {
                    await exit.ConfigureAwait(false);
                    break;
                }

                if (paused)
                {
                    job.PausedSeconds += (DateTime.UtcNow - pausedAt).TotalSeconds;
                }

                var busy = await ArcNeededAsync(ct).ConfigureAwait(false);
                if (busy && !paused && Signal(p.Id, SigStop))
                {
                    paused = true;
                    job.Phase = "paused";
                    _logger.LogInformation("Convert: paused {Path} while a recording, Live TV or a BroadcastHub capture needs the Arc", input);
                }
                else if (!busy && paused && Signal(p.Id, SigCont))
                {
                    paused = false;
                    job.Phase = "converting";
                    _logger.LogInformation("Convert: resumed {Path}", input);
                }
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (paused)
                {
                    Signal(p.Id, SigCont);
                }

                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            if (ct.IsCancellationRequested)
            {
                return (false, "stopped");
            }

            throw;
        }

        if (p.ExitCode == 255)
        {
            // Stopping Jellyfin signals every process in its group, so ffmpeg can quit a moment before
            // we hear about the shutdown. Give it a few seconds: if Jellyfin is stopping, this throws
            // and the movie goes back in the queue instead of being marked failed.
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }

        if (p.ExitCode != 0 || !File.Exists(output))
        {
            var tail = string.Join('\n', errors.TakeLast(8));
            _logger.LogWarning("Convert: ffmpeg exited {Code} for {Path}\n{Tail}", p.ExitCode, input, tail);
            return (false, $"ffmpeg stopped with an error ({p.ExitCode}); the original is untouched.");
        }

        return (true, string.Empty);
    }

    /// <summary>
    /// Reads the video's timestamps: a jump of more than 2 s means the recording lost signal there
    /// (Witch Hunt: 84 s gone at 27:30). About a minute for a two-hour movie. Jumps in the first
    /// <see cref="PluginConfiguration.ConvertIgnoreGapsSeconds"/> are the tuner settling, before the movie starts.
    /// </summary>
    public async Task<(string Health, string Text)> CheckGapsAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = _encoder.ProbePath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=dts_time", "-of", "csv=p=0", path })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe didn't start");
        _ = p.StandardError.ReadToEndAsync(ct);
        var ignore = Math.Max(0, Config.ConvertIgnoreGapsSeconds);
        double first = double.NaN, prev = double.NaN, biggest = 0, biggestAt = 0, lost = 0;
        var gaps = 0;
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (!double.TryParse(line.Trim().TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            {
                continue;
            }

            if (double.IsNaN(first))
            {
                first = t;
            }
            else if (t - prev > 2 && prev - first >= ignore)
            {
                gaps++;
                lost += t - prev;
                if (t - prev > biggest)
                {
                    biggest = t - prev;
                    biggestAt = prev - first;
                }
            }

            prev = t;
        }

        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (double.IsNaN(first))
        {
            return ("bad", "Couldn't read the video");
        }

        if (gaps == 0)
        {
            return ("ok", "No gaps");
        }

        var at = TimeSpan.FromSeconds(biggestAt);
        var size = TimeSpan.FromSeconds(biggest);
        return ("warn", gaps == 1
            ? $"Gap {Clock(size)} at {Clock(at)}"
            : $"{gaps} gaps, {Clock(TimeSpan.FromSeconds(lost))} lost (longest {Clock(size)} at {Clock(at)})");
    }

    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>True when the first video packet's timestamp is outside MPEG-TS's 33-bit clock (seen: exactly 2^33).</summary>
    private async Task<bool> BrokenFirstVideoPtsAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = _encoder.ProbePath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-read_intervals", "%+#1", "-show_entries", "packet=pts", "-of", "csv=p=0", path })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe didn't start");
            _ = p.StandardError.ReadToEndAsync(ct);
            var text = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var first = text.Split('\n', ',')[0].Trim();
            return long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pts) && pts >= 1L << 33;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Convert: couldn't read the first video timestamp of {Path}", path);
            return false;
        }
    }

    private async Task<(double Seconds, int Audio)> ProbeAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = _encoder.ProbePath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "json", path })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe didn't start");
        _ = p.StandardError.ReadToEndAsync(ct);
        var text = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var seconds = doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
        var audio = doc.RootElement.TryGetProperty("streams", out var st)
            ? st.EnumerateArray().Count(x => x.TryGetProperty("codec_type", out var c) && c.GetString() == "audio")
            : 0;
        return (seconds, audio);
    }

    private static long Free(string folder)
    {
        try
        {
            return new DriveInfo(folder).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---- store ------------------------------------------------------------------------------------

    private void Load()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                _entries = JsonSerializer.Deserialize<Dictionary<Guid, ConvertEntry>>(File.ReadAllText(_storePath)) ?? new();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't read {Path}; starting an empty convert list", _storePath);
        }
    }

    // Callers hold _lock.
    private void Save()
    {
        try
        {
            var tmp = _storePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Json));
            File.Move(tmp, _storePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't save {Path}", _storePath);
        }
    }
}
