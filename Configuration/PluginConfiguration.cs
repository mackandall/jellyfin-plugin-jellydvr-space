using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DvrSpace.Configuration;

/// <summary>An archive location recordings can be moved to, in order of preference.</summary>
public class ArchiveTarget
{
    /// <summary>Folder to move into; must be inside one of the libraries, or moved
    /// recordings would vanish from Jellyfin. Recordings keep their show/season layout below it.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>What it takes: "tv", "movies" or "both".</summary>
    public string Kind { get; set; } = "both";

    /// <summary>Never fill this location past this much free space (GB).</summary>
    public int KeepFreeGb { get; set; } = 50;
}

public class PluginConfiguration : BasePluginConfiguration
{
    public const string ModePreview = "preview";
    public const string ModeActive = "active";

    public const string ActionMove = "move";
    public const string ActionDelete = "delete";
    public const string ActionMoveUnwatchedDeleteWatched = "move-unwatched-delete-watched";

    public const string WhenFullStop = "stop";
    public const string WhenFullDelete = "delete";

    public const string OrderWatchedThenOldest = "watched-then-oldest";
    public const string OrderOldest = "oldest";
    public const string OrderMoviesFirst = "movies-first";

    /// <summary>"preview": only show what would happen. "active": actually move/delete.</summary>
    public string Mode { get; set; } = ModePreview;

    /// <summary>Keep at least this much free on the recordings drive (GB).</summary>
    public int KeepFreeGb { get; set; } = 100;

    /// <summary>How often to check (minutes); it also checks right after a recording finishes.</summary>
    public int CheckIntervalMinutes { get; set; } = 15;

    /// <summary>What happens to the recordings chosen to make room.</summary>
    public string Action { get; set; } = ActionMove;

    /// <summary>Archive locations, tried in order; the first with room gets the recording.</summary>
    public ArchiveTarget[] ArchiveTargets { get; set; } = Array.Empty<ArchiveTarget>();

    /// <summary>When every archive location is full or unreachable: "stop" (and warn) or "delete".</summary>
    public string WhenArchiveFull { get; set; } = WhenFullStop;

    /// <summary>Which recordings go first.</summary>
    public string Order { get; set; } = OrderWatchedThenOldest;

    /// <summary>Recordings marked Keep (library item ids): never moved or deleted.</summary>
    public string[] KeepItemIds { get; set; } = Array.Empty<string>();
}
