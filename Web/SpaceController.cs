using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSpace.Space;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrSpace.Web;

/// <summary>
/// Status, "check now" and Keep marks for the settings page. Admin only: this is what moves
/// and deletes recordings.
/// </summary>
[ApiController]
[Route("JellyDvrSpace")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class SpaceController : ControllerBase
{
    private readonly SpaceManager _manager;
    private readonly ILibraryManager _libraryManager;

    public SpaceController(SpaceManager manager, ILibraryManager libraryManager)
    {
        _manager = manager;
        _libraryManager = libraryManager;
    }

    /// <summary>The last plan (free space, what would be / was moved or deleted), recent actions.</summary>
    [HttpGet("Status")]
    public IActionResult Status()
    {
        var keep = (Plugin.Instance?.Configuration.KeepItemIds ?? Array.Empty<string>())
            .Select(id => Guid.TryParse(id, out var g) ? _libraryManager.GetItemById(g) : null)
            .Where(i => i is not null)
            .Select(i => new { Id = i!.Id.ToString("N"), i.Name, i.Path });
        return Ok(new
        {
            ManagedRoots = _manager.ManagedRoots(),
            Plan = _manager.LastPlan,
            Recent = _manager.RecentActions,
            Keep = keep
        });
    }

    /// <summary>Check now (and, in active mode, act); returns the new plan.</summary>
    [HttpPost("Run")]
    public async Task<IActionResult> Run(CancellationToken cancellationToken)
    {
        var plan = await _manager.RunAsync("manual", cancellationToken).ConfigureAwait(false);
        return Ok(plan);
    }

    /// <summary>Mark a recording Keep: never moved or deleted.</summary>
    [HttpPost("Keep/{itemId}")]
    public IActionResult Keep([FromRoute] Guid itemId) => SetKeep(itemId, true);

    [HttpDelete("Keep/{itemId}")]
    public IActionResult Unkeep([FromRoute] Guid itemId) => SetKeep(itemId, false);

    private IActionResult SetKeep(Guid itemId, bool keep)
    {
        if (Plugin.Instance is not { } plugin)
        {
            return NotFound();
        }

        var id = itemId.ToString("N");
        var ids = (plugin.Configuration.KeepItemIds ?? Array.Empty<string>())
            .Where(x => !string.Equals(x, id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (keep)
        {
            ids.Add(id);
        }

        plugin.Configuration.KeepItemIds = ids.ToArray();
        plugin.SaveConfiguration();
        return Ok(new { Keep = keep });
    }
}
