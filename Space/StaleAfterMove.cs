using System;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using static Jellyfin.Plugin.DvrSpace.Space.RecordingFileOps;

namespace Jellyfin.Plugin.DvrSpace.Space;

/// <summary>
/// A move that empties a library folder (the last movie out of the recordings' Movies folder) leaves
/// the old item behind: Jellyfin won't refresh a library folder that is empty ("inaccessible or empty,
/// skipping", its guard against a missing drive), so the recording shows twice. Shortly after each
/// move, once the new copy is in the library, the old item is taken out (its file is already gone;
/// nothing on disk is touched).
/// </summary>
public sealed partial class SpaceManager
{
    private static readonly TimeSpan[] StaleChecks = { TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30) };

    private void OnFileMoved(string from, string to)
    {
        if (!IsVideo(from))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var wait in StaleChecks)
            {
                await Task.Delay(wait).ConfigureAwait(false);
                try
                {
                    if (RemoveStale(from, to))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Checking for a leftover library item of {Path} failed", from);
                    return;
                }
            }
        });
    }

    // True when there's nothing more to do (removed, or Jellyfin dropped it itself, or the file came back).
    private bool RemoveStale(string from, string to)
    {
        var old = _libraryManager.FindByPath(from, false);
        if (old is null || File.Exists(from))
        {
            return true;
        }

        if (!File.Exists(to) || _libraryManager.FindByPath(to, false) is null)
        {
            return false; // the new copy isn't in the library yet: look again later
        }

        _libraryManager.DeleteItem(old, new DeleteOptions { DeleteFileLocation = false }, true);
        _logger.LogInformation("Removed the leftover library item of {Name} (moved to {To}; Jellyfin skips refreshing an emptied folder)", old.Name, to);
        return true;
    }
}
