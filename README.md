# JellyDVR Space (Jellyfin plugin)

Keeps the DVR's recordings drive from filling up. When free space drops below a floor, it moves
recordings to archive storage you list — or deletes them — in the order you choose, SageTV-style.
Part of the JellyDVR set alongside Comskip Commercial Segments, Episode History and XMLTV Guide Cleaner.

## What it manages

Only the DVR's recording folders from Live TV settings (recording, movie and series paths). Imported
archives elsewhere are never touched.

## Settings (Dashboard → Plugins → JellyDVR Space)

- **Space manager** — *Preview* (default): shows what it would do, nothing happens. *Active*: does it.
- **Keep at least** N GB free on the recordings drive (default 100). Checked every 15 minutes and two
  minutes after each recording finishes.
- **To make room** — move to archive storage (default), delete, or move unwatched / delete watched.
- **Which recordings go first** — watched then oldest (default), oldest first, or movies first.
- **Archive storage** — an ordered list of folders, each for TV, Movies, Sports or both, with its own
  free-space floor. A recording goes to the first one with room (a sports recording tries Sports
  locations first, then TV or Movies ones). A recording is sports when its guide categories match the
  **sports categories** list. Each must be inside a library, or moved recordings
  would vanish from Jellyfin; the page warns otherwise.
- **When every archive is full or unreachable** — stop and warn (default), or delete instead.
- **Live TV buffers** — recordings JellyDVR Live made as live buffers and nobody kept (tagged `Live buffer`)
  always go first and are deleted, never archived. Category sorting leaves them alone.
- **Keep** — recordings marked Keep (from the plan table) are never moved or deleted. Recordings still
  being recorded or being played are always left alone.

## Sort by guide category

Jellyfin's DVR files movies and series into their own folders; everything else (comedy specials, one-off
sports, …) lands in the general recording folder. Category rules move those out by their guide categories
(the item's genres plus the `<genre>` entries in the `.nfo` the DVR writes beside each recording):

- **Sorting** — *Off*, *Preview* (default: shows what would move) or *Active*.
- **Rules** — tried in order; the first one a recording matches decides where it goes. Each has a name,
  categories (comma separated), *any of them* / *all of them*, and a destination folder inside a library
  (empty = Live TV's movie recording folder). Ships with *Comedy specials* (`Special, Comedy`, all of them →
  movie folder) on, and *Sports* off: give it a destination (e.g. a Sports library folder) before turning it on, or it would use the movie folder.
- Recordings no rule matches stay where they are. Recordings still recording, being played, open in another
  program (e.g. Comskip still reading them) or marked Keep are left for the next check.

Sorting runs just before each space check (every few minutes and two minutes after a recording finishes),
and moves the same way as an archive move (below).

## Move to archive (item menu)

Admins get **Move to archive…** in a recording's "…" menu in the web client (only for items in the DVR's
recording folders). It opens a list of the archive locations that take that recording's kind, with free
space and where it would end up; locations that are unreachable, outside a library or too full are greyed
out with the reason, and one the move would push below its floor is marked. Picking one moves it in the
background with the same safe move as below. It works in Preview mode too (the click is the
confirmation), and refuses a recording that is still recording, being played or open in another program.
Needs `sudo ./install-web.sh` once (and after each jellyfin-web upgrade), which adds the script to
jellyfin-web's `index.html`.

## How a move works

The recording and its sidecars (`.edl`, `.nfo`, `-thumb.jpg`, …) are copied into the same show/season
layout under the archive folder (show/season `.nfo` and artwork copied only where missing), each file to a
temporary name, size-checked, then renamed; only then is the original removed. A name clash at the
destination aborts that recording untouched. Emptied show/season folders are removed and Jellyfin is told
about both folders, so the moved item keeps its watched state and resume point, and the Comskip plugin
re-imports its breaks from the moved `.edl`.

## Install

```
dotnet build -c Release
sudo ./install.sh   # restarts Jellyfin
sudo ./install-web.sh   # once: the item menu's "Move to archive…"
```

## License

GPL-2.0-or-later: you can redistribute and change it under the terms of the GNU General Public
License, version 2 or (at your option) any later version. See [LICENSE](LICENSE).

The official source and releases are at <https://github.com/mackandall/jellyfin-plugin-jellydvr-space>.
