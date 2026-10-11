using System;
using System.Linq;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.DvrSpace.Configuration;
using Jellyfin.Plugin.DvrSpace.Devices;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrSpace.Web;

/// <summary>What each JDVR client may do: a client asks for its own rights, the settings page lists them all.</summary>
[ApiController]
[Route("JellyDvrSpace")]
public sealed class DeviceRightsController : ControllerBase
{
    private readonly IDeviceManager _deviceManager;
    private readonly IUserManager _userManager;

    public DeviceRightsController(IDeviceManager deviceManager, IUserManager userManager)
    {
        _deviceManager = deviceManager;
        _userManager = userManager;
    }

    /// <summary>
    /// The calling device's rights, so the client can hide what it may not do. Only JDVR clients
    /// are managed; any other client is told it may do everything (its user's rights still apply).
    /// </summary>
    [HttpGet("Rights")]
    [Authorize]
    public IActionResult Rights()
    {
        var managed = DeviceRightsFilter.IsJdvr(HttpContext, out var deviceId);
        var r = managed ? DeviceRightsFilter.RightsOf(deviceId) : new DeviceRights { DeleteArchive = true };
        return Ok(new { Managed = managed, r.LiveTv, r.Record, r.Delete, r.DeleteArchive, r.Archive, r.Edit });
    }

    /// <summary>The JDVR clients that have signed in to this server, each with its rights, for the settings page.</summary>
    [HttpGet("Devices")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Devices()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var saved = cfg.DeviceRights ?? Array.Empty<DeviceRights>();
        // One row per sign-in in Jellyfin: a device used by two users is listed once.
        var known = _deviceManager.GetDevices(new DeviceQuery()).Items
            .Where(d => d.AppName is not null && d.AppName.StartsWith("JDVR", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(d.DeviceId))
            .GroupBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var last = g.OrderByDescending(d => d.DateLastActivity).First();
                var r = DeviceRightsFilter.RightsOf(g.Key);
                return new
                {
                    DeviceId = g.Key,
                    Name = last.DeviceName,
                    App = last.AppName + " " + last.AppVersion,
                    Users = g.Select(d => _userManager.GetUserById(d.UserId)?.Username).Where(n => n is not null).Distinct().ToList(),
                    LastActivity = (DateTime?)last.DateLastActivity,
                    Saved = saved.Any(s => string.Equals(s.DeviceId, g.Key, StringComparison.OrdinalIgnoreCase)),
                    r.LiveTv, r.Record, r.Delete, r.DeleteArchive, r.Archive, r.Edit
                };
            })
            .ToList();
        // Saved devices that have since signed out keep their rights for when they return.
        var gone = saved
            .Where(s => !known.Any(k => string.Equals(k.DeviceId, s.DeviceId, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new
            {
                s.DeviceId,
                s.Name,
                App = "signed out",
                Users = new System.Collections.Generic.List<string?>(),
                LastActivity = (DateTime?)null,
                Saved = true,
                s.LiveTv, s.Record, s.Delete, s.DeleteArchive, s.Archive, s.Edit
            });
        return Ok(new { Devices = known.OrderBy(d => d.Name).Concat(gone).ToList(), NewDevices = cfg.NewDeviceRights ?? new DeviceRights() });
    }
}
