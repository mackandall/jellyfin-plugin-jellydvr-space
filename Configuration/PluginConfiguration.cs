using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DvrSpace.Configuration;

/// <summary>An archive location recordings can be moved to, in order of preference.</summary>
public class ArchiveTarget
{
    /// <summary>Folder to move into; must be inside one of the libraries, or moved
    /// recordings would vanish from Jellyfin. Recordings keep their show/season layout below it.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>What it takes: "tv", "movies", "sports" or "both". A sports recording can also go
    /// to a "tv" or "movies" location (whichever it is), but "sports" ones are offered first.</summary>
    public string Kind { get; set; } = "both";

    /// <summary>Never fill this location past this much free space (GB).</summary>
    public int KeepFreeGb { get; set; } = 50;
}

/// <summary>
/// A sorting rule for recordings that land in the DVR's general recording folder (not a movie,
/// not a series): recordings whose guide categories match are moved to <see cref="Destination"/>.
/// </summary>
public class CategoryRule
{
    /// <summary>A label for the rule, e.g. "Comedy specials".</summary>
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Guide categories (genres), separated by commas; case doesn't matter.</summary>
    public string Categories { get; set; } = string.Empty;

    /// <summary>True: the recording needs every listed category. False: any one of them.</summary>
    public bool RequireAll { get; set; }

    /// <summary>Folder to move into, inside a library. Empty: Live TV's movie recording folder.</summary>
    public string Destination { get; set; } = string.Empty;
}

/// <summary>
/// What one JDVR client (a TV) may do. These only narrow what its signed-in user may do in
/// Jellyfin; they never add to it.
/// </summary>
public class DeviceRights
{
    /// <summary>Jellyfin's device id of the client; empty in <see cref="PluginConfiguration.NewDeviceRights"/>.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>The device's name when it was saved, for the settings page.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Watch live TV.</summary>
    public bool LiveTv { get; set; } = true;

    /// <summary>Record and schedule: timers, series recordings, Recording Favourites.</summary>
    public bool Record { get; set; } = true;

    /// <summary>Delete recordings in the DVR's recordings folders.</summary>
    public bool Delete { get; set; } = true;

    /// <summary>Delete from the libraries outside the recordings folders (the archive).</summary>
    public bool DeleteArchive { get; set; }

    /// <summary>Move recordings to the archive.</summary>
    public bool Archive { get; set; } = true;

    /// <summary>Edit recordings: commercial scan, program start, audio format.</summary>
    public bool Edit { get; set; } = true;
}

public class PluginConfiguration : BasePluginConfiguration
{
    public const string SortOff = "off";
    public const string SortPreview = "preview";
    public const string SortActive = "active";

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

    /// <summary>
    /// Delete playback copies left in Jellyfin's transcode folder once nothing has written them
    /// for this many hours and no playback is using them (hours; 0 leaves them to Jellyfin).
    /// </summary>
    public int TranscodeSweepHours { get; set; } = 6;

    // ---- Convert to HEVC (the Convert page) ----

    /// <summary>Movies at or above this bitrate (Mb/s, H.264, up to 1080p, .ts/.mpg) are listed for converting.</summary>
    public int ConvertMinMbps { get; set; } = 12;

    /// <summary>The HEVC video bitrate (Mb/s); 9 passed the eye test against the originals.</summary>
    public int ConvertMbps { get; set; } = 9;

    /// <summary>Conversions start only from this hour (0–23; -1 any time)…</summary>
    public int ConvertFromHour { get; set; } = 1;

    /// <summary>…until this hour (a movie started before it finishes).</summary>
    public int ConvertToHour { get; set; } = 7;

    /// <summary>Don't start a conversion while a tuner is recording.</summary>
    public bool ConvertPauseWhileRecording { get; set; } = true;

    /// <summary>Days the original is kept (hidden, beside the movie) for Undo; 0 deletes it at once.</summary>
    public int ConvertKeepDays { get; set; } = 14;

    /// <summary>Gaps this many seconds into a recording are ignored by the gap check (the tuner settling; 0 counts them all).</summary>
    public int ConvertIgnoreGapsSeconds { get; set; } = 30;

    /// <summary>The Arc card's render node (the iGPU is renderD128).</summary>
    public string ConvertDevice { get; set; } = "/dev/dri/renderD129";

    /// <summary>What happens to the recordings chosen to make room.</summary>
    public string Action { get; set; } = ActionMove;

    /// <summary>Archive locations, tried in order; the first with room gets the recording.</summary>
    public ArchiveTarget[] ArchiveTargets { get; set; } = Array.Empty<ArchiveTarget>();

    /// <summary>When every archive location is full or unreachable: "stop" (and warn) or "delete".</summary>
    public string WhenArchiveFull { get; set; } = WhenFullStop;

    /// <summary>Which recordings go first.</summary>
    public string Order { get; set; } = OrderWatchedThenOldest;

    /// <summary>Guide categories that make a recording "sports" for archiving, separated by commas.</summary>
    public string SportsCategories { get; set; } = "Sports, Sports event, Sporting event, Sports non-event, Sports talk";

    /// <summary>Recordings marked Keep (library item ids): never moved or deleted.</summary>
    public string[] KeepItemIds { get; set; } = Array.Empty<string>();

    /// <summary>Sorting "other" recordings by guide category: "off", "preview" or "active".</summary>
    public string SortMode { get; set; } = SortPreview;

    /// <summary>Category rules, tried in order; the first match decides where a recording goes.</summary>
    public CategoryRule[] CategoryRules { get; set; } =
    {
        new() { Name = "Comedy specials", Categories = "Special, Comedy", RequireAll = true },
        new() { Name = "Sports", Enabled = false, Categories = "Sports, Sports event, Sporting event" }
    };

    /// <summary>Rights of the JDVR clients set on the settings page, one per device.</summary>
    public DeviceRights[] DeviceRights { get; set; } = Array.Empty<DeviceRights>();

    /// <summary>Rights of a JDVR client that isn't in <see cref="DeviceRights"/> (one not saved yet).</summary>
    public DeviceRights NewDeviceRights { get; set; } = new();
}
