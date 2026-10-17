using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DvrSpace.Space;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrSpace.Web;

/// <summary>The Convert page: the list, the movie being converted, and the queue's actions. Admin only.</summary>
[ApiController]
[Route("JellyDvrSpace/Convert")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class ConvertController : ControllerBase
{
    private readonly Converter _converter;

    public ConvertController(Converter converter)
    {
        _converter = converter;
    }

    public sealed record IdsBody(List<Guid> Ids);

    /// <summary>Every movie on the list with its state, plus the one converting now.</summary>
    [HttpGet]
    public IActionResult List([FromQuery] bool fresh = false)
    {
        var job = _converter.Current;
        return Ok(new
        {
            Rows = _converter.Rows(fresh).Select(r => new
            {
                Id = r.Id.ToString("N"),
                r.Name,
                r.Year,
                r.Where,
                r.Bytes,
                r.Minutes,
                r.Mbps,
                r.AfterBytes,
                r.Recorded,
                r.Entry.Status,
                r.Entry.Health,
                r.Entry.HealthText,
                r.Entry.Message,
                r.Entry.OriginalBytes,
                r.Entry.NewBytes,
                r.Entry.UndoUntilUtc,
                KeptBytes = r.Entry.BackupPath is null ? 0 : r.Entry.OriginalBytes
            }),
            Current = job is null ? null : new { Id = job.Id.ToString("N"), job.Name, job.Where, job.Minutes, job.Done, job.Speed, job.Phase, job.StartedUtc },
            _converter.Waiting
        });
    }

    [HttpPost("Queue")]
    public IActionResult Queue([FromBody] IdsBody body)
    {
        _converter.Queue(body.Ids);
        return Ok();
    }

    [HttpPost("Check")]
    public IActionResult Check([FromBody] IdsBody body)
    {
        _converter.Check(body.Ids);
        return Ok(new { Message = $"Checking {body.Ids.Count} movie{(body.Ids.Count == 1 ? string.Empty : "s")} for gaps; about a minute each." });
    }

    [HttpPost("{id}/Skip")]
    public IActionResult Skip([FromRoute] Guid id)
    {
        _converter.SetStatus(id, ConvertEntry.Skipped);
        return Ok();
    }

    [HttpPost("{id}/Unskip")]
    public IActionResult Unskip([FromRoute] Guid id)
    {
        _converter.SetStatus(id, ConvertEntry.Todo);
        return Ok();
    }

    [HttpPost("{id}/Unqueue")]
    public IActionResult Unqueue([FromRoute] Guid id)
    {
        _converter.SetStatus(id, ConvertEntry.Todo);
        return Ok();
    }

    [HttpPost("Stop")]
    public IActionResult Stop()
    {
        _converter.Stop();
        return Ok();
    }

    [HttpPost("{id}/Undo")]
    public IActionResult Undo([FromRoute] Guid id) => Ok(new { Message = _converter.Undo(id) });
}
