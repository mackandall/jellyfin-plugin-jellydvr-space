# JellyDVR Space (Jellyfin plugin)

Keeps the DVR's recordings drive from filling up. When free space drops below a floor, it moves
recordings to archive storage you list — or deletes them — in the order you choose, SageTV-style.
Part of the JellyDVR set alongside Comskip Commercial Segments, Episode History and XMLTV Guide Cleaner.

## What it manages

Only the DVR's recording folders from Live TV settings (recording, movie and series paths). Imported
archives elsewhere are never touched.

## Settings (Dashboard → Plugins → JellyDVR Space)

- **Mode** — *Preview* (default): shows what it would do, nothing happens. *Active*: does it.
- **Keep at least** N GB free on the recordings drive (default 100). Checked every 15 minutes and two
  minutes after each recording finishes.
- **To make room** — move to archive storage (default), delete, or move unwatched / delete watched.
- **Which recordings go first** — watched then oldest (default), oldest first, or movies first.
- **Archive storage** — an ordered list of folders, each for TV, Movies or both, with its own free-space
  floor. A recording goes to the first one with room. Each must be inside a library, or moved recordings
  would vanish from Jellyfin; the page warns otherwise.
- **When every archive is full or unreachable** — stop and warn (default), or delete instead.
- **Keep** — recordings marked Keep (from the plan table) are never moved or deleted. Recordings still
  being recorded or being played are always left alone.

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
```
