using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSpace.Configuration;
using Jellyfin.Plugin.DvrSpace.Space;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.DvrSpace.Devices;

/// <summary>
/// Refuses what a JDVR client isn't allowed to do (see <see cref="DeviceRights"/>), on every
/// request to the server: Jellyfin's own calls and the JellyDVR plugins' alike. Requests from
/// any other client pass untouched. The client and device come from the request's sign-in, so
/// this guards a TV's remote, not someone crafting requests by hand.
/// </summary>
public sealed class DeviceRightsFilter : IAsyncResourceFilter
{
    private readonly ILibraryManager _libraryManager;
    private readonly SpaceManager _manager;

    public DeviceRightsFilter(ILibraryManager libraryManager, SpaceManager manager)
    {
        _libraryManager = libraryManager;
        _manager = manager;
    }

    /// <summary>Whether the request comes from a JDVR client, and from which device.</summary>
    public static bool IsJdvr(HttpContext http, out string deviceId)
    {
        deviceId = http.User.FindFirst("Jellyfin-DeviceId")?.Value ?? string.Empty;
        var client = http.User.FindFirst("Jellyfin-Client")?.Value;
        return client is not null && client.StartsWith("JDVR", StringComparison.OrdinalIgnoreCase) && deviceId.Length > 0;
    }

    /// <summary>The rights of a JDVR device: its own once saved on the settings page, else those of new devices.</summary>
    public static DeviceRights RightsOf(string deviceId)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return (cfg.DeviceRights ?? Array.Empty<DeviceRights>())
            .FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            ?? cfg.NewDeviceRights
            ?? new DeviceRights();
    }

    // A resource filter: after the sign-in is known, before the request's body is even read.
    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (IsJdvr(http, out var deviceId))
        {
            var rights = RightsOf(deviceId);
            var refused = Needed(http.Request).FirstOrDefault(n => !n.Allowed(rights));
            if (refused is not null)
            {
                context.Result = new ObjectResult(new { Message = $"This device isn't allowed to {refused.What}. An administrator can change that in JellyDVR Space's settings." })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
                return Task.CompletedTask;
            }
        }

        return next();
    }

    private sealed record Need(Func<DeviceRights, bool> Allowed, string What);

    private static readonly Need LiveTv = new(r => r.LiveTv, "watch live TV");
    private static readonly Need Record = new(r => r.Record, "record or change recordings");
    private static readonly Need Delete = new(r => r.Delete, "delete recordings");
    private static readonly Need DeleteArchive = new(r => r.DeleteArchive, "delete from the archive libraries");
    private static readonly Need Archive = new(r => r.Archive, "move recordings to the archive");
    private static readonly Need Edit = new(r => r.Edit, "edit recordings");

    /// <summary>The rights a request needs; none for anything that only reads.</summary>
    private IEnumerable<Need> Needed(HttpRequest request)
    {
        var seg = (request.Path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length == 0)
        {
            return Array.Empty<Need>();
        }

        bool Is(int i, string name) => seg.Length > i && string.Equals(seg[i], name, StringComparison.OrdinalIgnoreCase);
        bool Flag(string name) => string.Equals(request.Query[name].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var delete = HttpMethods.IsDelete(request.Method);
        var writes = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);

        if (Is(0, "Items"))
        {
            if (delete)
            {
                // DELETE /Items/{id}, or /Items?ids=a,b.
                var ids = seg.Length > 1 ? new[] { seg[1] } : request.Query["ids"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
                return ids.Select(DeleteOf).Distinct().ToList();
            }

            // Tuning a channel starts with its playback info.
            if (Is(2, "PlaybackInfo") && Guid.TryParse(seg[1], out var id) && _libraryManager.GetItemById(id) is LiveTvChannel)
            {
                return new[] { LiveTv };
            }
        }
        else if (Is(0, "LiveTv") && writes)
        {
            if (Is(1, "Timers") || Is(1, "SeriesTimers"))
            {
                return new[] { Record };
            }

            if (Is(1, "Recordings") && delete && seg.Length > 2)
            {
                return new[] { DeleteOf(seg[2]) };
            }
        }
        else if (Is(0, "EpisodeHistory") && delete)
        {
            if (Is(1, "Item") && seg.Length > 3 && (Is(3, "Recording") || (Is(3, "History") && Flag("deleteRecording"))))
            {
                return Flag("dryRun") ? Array.Empty<Need>() : new[] { DeleteOf(seg[2]) };
            }

            if (Is(1, "Recordings") && Is(2, "Series") && !Flag("dryRun"))
            {
                return new[] { Flag("archive") ? DeleteArchive : Delete };
            }
        }
        else if (Is(0, "JellyDvrSpace") && writes && Is(1, "Archive"))
        {
            return new[] { Archive };
        }
        else if (Is(0, "JellyDvrSchedule") && writes && !Is(2, "Preview"))
        {
            return new[] { Record };
        }
        else if (Is(0, "JellyDvrLive") && writes)
        {
            if (Is(1, "Keep"))
            {
                return new[] { Record };
            }

            // Never Stop: that is what frees the tuner.
            if (Is(1, "Channel") && Is(3, "Start"))
            {
                return new[] { LiveTv };
            }
        }
        else if (Is(0, "ComskipSegments") && writes)
        {
            return new[] { Edit };
        }

        return Array.Empty<Need>();
    }

    /// <summary>Deleting this item: a recording (in the DVR's recordings folders), or an archive copy.</summary>
    private Need DeleteOf(string itemId)
    {
        var path = Guid.TryParse(itemId, out var id) ? _libraryManager.GetItemById(id)?.Path : null;
        return IsRecording(path) ? Delete : DeleteArchive;
    }

    private bool IsRecording(string? path) =>
        !string.IsNullOrEmpty(path)
        && _manager.ManagedRoots().Any(r => path.StartsWith(r.TrimEnd('/') + "/", StringComparison.Ordinal));
}
