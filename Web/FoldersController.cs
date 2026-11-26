using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.DvrSpace.Configuration;
using Jellyfin.Plugin.DvrSpace.Space;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static Jellyfin.Plugin.DvrSpace.Space.RecordingFileOps;

namespace Jellyfin.Plugin.DvrSpace.Web;

/// <summary>
/// The settings page's folder picker for storage folders: folders as the server sees them, which
/// of them are drives (mount points) and how full, plus making a new folder. Admin only.
/// Mounting a drive is the server's business, not this plugin's.
/// </summary>
[ApiController]
[Route("JellyDvrSpace")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class FoldersController : ControllerBase
{
    private const int MaxFolders = 500;

    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "lost+found", "$RECYCLE.BIN", "System Volume Information", "#recycle", "@eaDir", "#snapshot"
    };

    private readonly SpaceManager _manager;
    private readonly ILibraryManager _libraryManager;

    public FoldersController(SpaceManager manager, ILibraryManager libraryManager)
    {
        _manager = manager;
        _libraryManager = libraryManager;
    }

    /// <summary>The folders inside <paramref name="path"/> (default /mnt), drives marked with their free space.</summary>
    [HttpGet("Folders")]
    public IActionResult Folders([FromQuery] string? path)
    {
        path = string.IsNullOrWhiteSpace(path) ? (Directory.Exists("/mnt") ? "/mnt" : "/") : path.Trim();
        if (!Path.IsPathRooted(path))
        {
            return BadRequest("The path must start with /.");
        }

        path = Path.GetFullPath(path);
        if (path.Length > 1)
        {
            path = path.TrimEnd('/');
        }

        var listing = SpaceManager.WithTimeout(() => Directory.Exists(path)
            ? Directory.EnumerateDirectories(path).Take(MaxFolders * 2).ToList()
            : null);
        if (!listing.Completed)
        {
            return Ok(new { Path = path, Parent = Parent(path), Problem = "not responding (a network drive that is down?)", Folders = Array.Empty<object>() });
        }

        if (listing.Value is null)
        {
            return Ok(new { Path = path, Parent = Parent(path), Problem = "not found", Folders = Array.Empty<object>() });
        }

        var mounts = Mounts();
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var storage = (cfg.ArchiveTargets ?? Array.Empty<ArchiveTarget>())
            .Select(t => (t.Path ?? string.Empty).Trim().TrimEnd('/'))
            .Where(p => p.Length > 0)
            .ToList();
        var libraries = _libraryManager.GetVirtualFolders()
            .SelectMany(f => (f.Locations ?? Array.Empty<string>()).Select(l => (f.Name, Path: l.TrimEnd('/'))))
            .ToList();

        var folders = listing.Value
            .Select(d => d.TrimEnd('/'))
            .Where(d => Path.GetFileName(d) is { Length: > 0 } n && !n.StartsWith('.') && !Hidden.Contains(n))
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .Take(MaxFolders)
            .Select(d =>
            {
                mounts.TryGetValue(d, out var source);
                var space = source is null ? default : SpaceManager.WithTimeout(() => SpaceManager.FreeSpace(d));
                return new
                {
                    Name = Path.GetFileName(d),
                    Path = d,
                    IsDrive = source is not null,
                    Source = source,
                    FreeBytes = space.Value?.Free,
                    TotalBytes = space.Value?.Total,
                    NotResponding = source is not null && !space.Completed,
                    UsedBySpace = storage.Any(s => s == d || IsUnder(s, d)),
                    Library = libraries.FirstOrDefault(l => l.Path == d || IsUnder(d, l.Path)).Name
                };
            })
            .ToList();

        var here = SpaceManager.WithTimeout(() => SpaceManager.FreeSpace(path));
        return Ok(new
        {
            Path = path,
            Parent = Parent(path),
            FreeBytes = here.Value?.Free,
            TotalBytes = here.Value?.Total,
            Empty = listing.Value.Count == 0 && SpaceManager.WithTimeout(() => (bool?)!Directory.EnumerateFileSystemEntries(path).Any()).Value == true,
            Library = libraries.FirstOrDefault(l => l.Path == path || IsUnder(path, l.Path)).Name,
            UsedBySpace = storage.Any(s => s == path),
            Truncated = listing.Value.Count > MaxFolders,
            Folders = folders
        });
    }

    /// <summary>Makes a folder (and any missing parents below an existing folder).</summary>
    [HttpPost("Folders")]
    public IActionResult MakeFolder([FromBody] FolderRequest request)
    {
        var path = (request?.Path ?? string.Empty).Trim();
        if (!Path.IsPathRooted(path) || path.Split('/').Any(s => s == ".."))
        {
            return BadRequest("The path must start with / and can't contain '..'.");
        }

        path = Path.GetFullPath(path).TrimEnd('/');
        var done = SpaceManager.WithTimeout<string>(() =>
        {
            try
            {
                Directory.CreateDirectory(path);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return ex.Message;
            }
        });
        if (!done.Completed)
        {
            return StatusCode(504, "The drive is not responding.");
        }

        return done.Value is null ? Ok(new { Path = path }) : BadRequest(done.Value);
    }

    /// <summary>Status of storage folders as the page has them, saved or not.</summary>
    [HttpPost("FolderStatus")]
    public IActionResult FolderStatus([FromBody] FolderStatusRequest request) =>
        Ok(_manager.CheckFolders(request?.Folders ?? Array.Empty<ArchiveTarget>()));

    private static string? Parent(string path) => path == "/" ? null : (Path.GetDirectoryName(path) ?? "/");

    // Mount point → what is mounted there (device or share), from /proc/mounts (Linux).
    private static Dictionary<string, string> Mounts()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in System.IO.File.ReadLines("/proc/mounts"))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 3)
                {
                    result[Unescape(parts[1])] = Unescape(parts[0]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not Linux: no drives marked; folders still list.
        }

        return result;
    }

    // /proc/mounts writes space, tab, newline and backslash as octal escapes (\040 …).
    private static string Unescape(string s)
    {
        if (!s.Contains('\\', StringComparison.Ordinal))
        {
            return s;
        }

        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && IsOctal(s, i + 1))
            {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }

    private static bool IsOctal(string s, int at) =>
        at + 3 <= s.Length && s.Substring(at, 3).All(c => c is >= '0' and <= '7');

    public sealed class FolderRequest
    {
        public string? Path { get; set; }
    }

    public sealed class FolderStatusRequest
    {
        public ArchiveTarget[]? Folders { get; set; }
    }
}
