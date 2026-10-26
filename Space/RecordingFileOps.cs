using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSpace.Space;

/// <summary>
/// File work shared by the space manager and the category sorter: finding a recording's
/// sidecars and moving it (with them) safely into another folder.
/// </summary>
internal static class RecordingFileOps
{
    /// <summary>Each file a move put somewhere else (from, to), once the move is complete.</summary>
    public static event Action<string, string>? FileMoved;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".mkv", ".mp4", ".m4v", ".m2ts", ".mpg", ".mpeg", ".avi", ".wtv", ".mov"
    };

    /// <summary>
    /// Moves one recording and its sidecars (.edl, .nfo, -thumb.jpg, …) into
    /// <paramref name="destDir"/>, copying the show/season .nfo and artwork there if missing.
    /// Copies to a temporary name, checks the size, then renames and removes the original, so
    /// a failure part-way never loses the recording. Leaves folders emptied of recordings tidy.
    /// </summary>
    public static void MoveRecording(string videoPath, string root, string destDir, ILibraryMonitor libraryMonitor, ILogger logger)
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

        foreach (var src in files)
        {
            FileMoved?.Invoke(src, Path.Combine(destDir, Path.GetFileName(src)));
        }

        var srcDir = Path.GetDirectoryName(videoPath)!;
        TidyEmptiedFolders(srcDir, root, logger);
        libraryMonitor.ReportFileSystemChanged(srcDir);
        libraryMonitor.ReportFileSystemChanged(destDir);
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
    public static void TidyEmptiedFolders(string dir, string root, ILogger logger)
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
                logger.LogDebug(ex, "Could not remove emptied folder {Dir}", dir);
                return;
            }

            dir = Path.GetDirectoryName(dir)!;
        }
    }

    /// <summary>
    /// The recording plus its sidecars: same base name followed by "." or "-", and the originals
    /// kept hidden beside it for Undo (".name.original.ts" from the Comskip plugin's trims and cuts,
    /// with the .edl it keeps for it, ".name.h264-original.ts" from Convert to HEVC), so they move
    /// and are deleted with it.
    /// </summary>
    public static List<string> RecordingFiles(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath)!;
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var files = Directory.GetFiles(dir);
        // Another recording whose name starts with this one's ("About Last Night...ts" and
        // "About Last Night....ts", the piece of a restarted recording) is not a sidecar, and
        // neither are its own sidecars.
        var longer = files.Where(f => f != videoPath && IsVideo(f))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n!.Length > baseName.Length && BelongsTo(n + ".", baseName))
            .ToList();
        return files
            .Where(f => f == videoPath
                || (BelongsTo(Path.GetFileName(f), baseName) && !longer.Any(n => BelongsTo(Path.GetFileName(f), n!)))
                || IsHiddenOriginal(Path.GetFileName(f), baseName))
            .Where(f => !f.EndsWith(".jdvrpart", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Whether any process has the file open — Comskip still reading it, ffmpeg probing it —
    /// found through /proc/*/fd. Moving it then would leave that program writing its results
    /// (.edl, …) into a folder that's gone.
    /// </summary>
    public static bool IsOpen(string path)
    {
        try
        {
            foreach (var proc in Directory.EnumerateDirectories("/proc"))
            {
                if (!char.IsDigit(Path.GetFileName(proc)[0]))
                {
                    continue;
                }

                try
                {
                    foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(proc, "fd")))
                    {
                        if (string.Equals(new FileInfo(fd).LinkTarget, path, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another user's process, or one that just exited.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static bool IsHiddenOriginal(string fileName, string baseName) =>
        fileName.StartsWith("." + baseName + ".original.", StringComparison.Ordinal)
        || fileName.StartsWith("." + baseName + ".h264-original.", StringComparison.Ordinal);

    private static bool BelongsTo(string fileName, string baseName) =>
        fileName.StartsWith(baseName + ".", StringComparison.Ordinal) || fileName.StartsWith(baseName + "-", StringComparison.Ordinal);

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsUnder(string path, string root) =>
        path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);

    public static long SafeLength(string path)
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

    public static void TryDelete(string path)
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
}
