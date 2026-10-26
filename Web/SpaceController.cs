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
    private readonly CategorySorter _sorter;
    private readonly ILibraryManager _libraryManager;

    public SpaceController(SpaceManager manager, CategorySorter sorter, ILibraryManager libraryManager)
    {
        _manager = manager;
        _sorter = sorter;
        _libraryManager = libraryManager;
    }

    /// <summary>The last plans (free space; category sorting), recent actions.</summary>
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
            Sort = _sorter.LastPlan,
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

    /// <summary>Recordings being moved to the archive right now, with progress.</summary>
    [HttpGet("Moving")]
    public IActionResult Moving() => Ok(_manager.Moving());

    /// <summary>For the item menu's "Move to archive…": is it a recording, and where can it go.</summary>
    [HttpGet("Archive/{itemId}")]
    public IActionResult ArchiveChoices([FromRoute] Guid itemId) =>
        _libraryManager.GetItemById(itemId) is { } item ? Ok(_manager.GetArchiveChoices(item)) : NotFound();

    /// <summary>Moves one recording to the archive location <paramref name="target"/> (in the background).</summary>
    [HttpPost("Archive/{itemId}")]
    public IActionResult Archive([FromRoute] Guid itemId, [FromQuery] string target)
    {
        if (_libraryManager.GetItemById(itemId) is not { } item)
        {
            return NotFound(new { Message = "No such item." });
        }

        try
        {
            return Ok(new { Message = _manager.StartArchive(item, target ?? string.Empty) });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { ex.Message });
        }
    }

    /// <summary>"Store this show in…": the show of any of its items, its folders, and every storage folder.</summary>
    [HttpGet("ShowStorage/{itemId}")]
    public IActionResult ShowStorage([FromRoute] Guid itemId) =>
        _libraryManager.GetItemById(itemId) is { } item ? Ok(_manager.GetShowStorage(item)) : NotFound();

    /// <summary>Saves a show's storage folders, in order (none: it stays on the recording drive).</summary>
    [HttpPost("ShowStorage")]
    public IActionResult SetShowStorage([FromBody] ShowStorageRequest request)
    {
        try
        {
            return Ok(new { Message = _manager.SetShowStorage(request.Show ?? string.Empty, request.Locations ?? Array.Empty<string>()) });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { ex.Message });
        }
    }

    /// <summary>Saves one programme's or recording's own folders: Mode "show" (use the show's setting), "drive" or "folders".</summary>
    [HttpPost("RecordingStorage")]
    public IActionResult SetRecordingStorage([FromBody] RecordingStorageRequest request)
    {
        if (_libraryManager.GetItemById(request.ItemId) is not { } item)
        {
            return NotFound(new { Message = "No such item." });
        }

        try
        {
            return Ok(new { Message = _manager.SetRecordingStorage(item, request.Mode ?? "show", request.Locations ?? Array.Empty<string>()) });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { ex.Message });
        }
    }

    public sealed class RecordingStorageRequest
    {
        public Guid ItemId { get; set; }

        public string? Mode { get; set; }

        public string[]? Locations { get; set; }
    }

    public sealed class ShowStorageRequest
    {
        public string? Show { get; set; }

        public string[]? Locations { get; set; }
    }

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
