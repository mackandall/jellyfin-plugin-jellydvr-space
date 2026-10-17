using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSpace.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSpace.Space;

/// <summary>
/// Deletes playback copies Jellyfin left in its transcode folder. A web player that closes or
/// moves on before its "stopped" report gets through leaves the job's files behind (a remux
/// writes the whole movie in a minute, 6 GB or more), and Jellyfin's own clean-up only removes
/// files a day old. A job's files (one name, many segments) go once nothing has written them for
/// <see cref="PluginConfiguration.TranscodeSweepHours"/> and Jellyfin has no transcoding job for
/// them any more.
/// </summary>
public sealed class TranscodeSweeper : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    private readonly ILogger<TranscodeSweeper> _logger;
    private readonly IConfigurationManager _config;
    private readonly ITranscodeManager _transcodes;

    public TranscodeSweeper(ILogger<TranscodeSweeper> logger, IConfigurationManager config, ITranscodeManager transcodes)
    {
        _logger = logger;
        _config = config;
        _transcodes = transcodes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Sweep();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Transcode folder clean-up failed");
                }

                await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Deletes the stale jobs' files; returns the bytes freed.</summary>
    public long Sweep()
    {
        var hours = Plugin.Instance?.Configuration.TranscodeSweepHours ?? 0;
        var folder = _config.GetTranscodePath();
        if (hours <= 0 || !Directory.Exists(folder))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddHours(-hours);
        long freed = 0;
        foreach (var job in Jobs(folder))
        {
            if (job.Files.Max(f => f.LastWriteTimeUtc) > cutoff || InUse(folder, job.Name))
            {
                continue;
            }

            long bytes = 0;
            foreach (var f in job.Files)
            {
                try
                {
                    var length = f.Length;
                    f.Delete();
                    bytes += length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Could not delete {Path}", f.FullName);
                }
            }

            freed += bytes;
            _logger.LogInformation(
                "Deleted a leftover playback copy {Name} in {Folder}: {Count} files, {Mb:N0} MB",
                job.Name, folder, job.Files.Count, bytes / 1048576);
        }

        return freed;
    }

    // A job's files share its 32-character id: "<id>.m3u8", "<id>12.mp4", "<id>-1.mp4", "<id>.ts".
    private static IEnumerable<(string Name, List<FileInfo> Files)> Jobs(string folder)
    {
        return new DirectoryInfo(folder)
            .EnumerateFiles()
            .Where(f => f.Name.Length > 32 && IsHex(f.Name.AsSpan(0, 32)))
            .GroupBy(f => f.Name[..32], StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.ToList()));
    }

    // Jellyfin still has a job writing or serving these (a live stream, a long pause).
    private bool InUse(string folder, string name)
    {
        var playlist = Path.Combine(folder, name + ".m3u8");
        return _transcodes.GetTranscodingJob(playlist) is not null
            || _transcodes.GetTranscodingJob(Path.Combine(folder, name + ".ts")) is not null
            || _transcodes.GetTranscodingJob(Path.Combine(folder, name + ".mkv")) is not null;
    }

    private static bool IsHex(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
