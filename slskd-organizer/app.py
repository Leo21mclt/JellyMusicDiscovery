"""
slskd-organizer
===============

Tiny service that watches the slskd downloads directory and moves
completed audio files into a music-library tree organized by
{Artist}/{Album}/{TrackNum - Title}.{ext}, using the file's own ID3/Vorbis
tags. Designed to run as a docker-compose service alongside slskd; both
containers mount the same host paths.

Why a separate container instead of the Jellyfin plugin doing this?
The plugin runs inside Jellyfin's container, which doesn't have
slskd's downloads mounted. A standalone container only needs to share
the two host paths (downloads and music) and runs independently of
Jellyfin entirely — it can keep organizing even while Jellyfin is down,
and reorganize the whole downloads dir on first startup.

Behavior:
- Polls every POLL_INTERVAL_SEC seconds (default 30s)
- For each audio file in DOWNLOADS_ROOT (recursively), reads tags
- Falls back to filename-based heuristics if tags are missing
- Moves to MUSIC_ROOT/{Artist}/{Album}/{NN - }Title.{ext}
- Sanitizes filename components (illegal chars → _)
- Resolves collisions with " (2)", " (3)", etc.
- Removes empty source dirs after moving

Requires: mutagen (apt: python3-mutagen, pip: mutagen).
"""
from __future__ import annotations

import logging
import os
import re
import shutil
import sys
import time
import urllib.request
import urllib.error
from pathlib import Path
from typing import Optional

import mutagen
from mutagen.easyid3 import EasyID3
from mutagen.flac import FLAC
from mutagen.mp4 import MP4

# --- config (env-driven so docker-compose can override without code changes) -

DOWNLOADS_ROOT = Path(os.environ.get("DOWNLOADS_ROOT", "/data/downloads"))
MUSIC_ROOT     = Path(os.environ.get("MUSIC_ROOT",     "/data/music"))
POLL_INTERVAL  = int(os.environ.get("POLL_INTERVAL_SEC", "30"))

# Optional: ping Jellyfin's library-refresh endpoint after moving files so
# the new track shows up immediately rather than on the next scheduled
# scan. Both env vars must be set for this to fire. The API key needs
# Library:Manage permission (or be an admin token).
JELLYFIN_URL     = os.environ.get("JELLYFIN_URL",     "").rstrip("/")
JELLYFIN_API_KEY = os.environ.get("JELLYFIN_API_KEY", "")
# Debounce: when many files arrive in one scan we only want to fire ONE
# library refresh, not N. Track if any move happened in the current scan
# and trigger once at the end.

AUDIO_EXTS = {".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav"}

# Ignore in-progress / temp files. slskd writes to a `.incomplete` filename
# while transferring, then renames on completion.
IGNORE_SUFFIXES = {".part", ".incomplete", ".tmp", ".crdownload"}

# Subdirs of MUSIC_ROOT we should never recurse INTO when sanity-checking
# move targets — these are the standard discovery-plugin scratch areas.
SKIP_DIRS_IN_MUSIC = {".discovery-downloads"}

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
)
log = logging.getLogger("slskd-organizer")


# --- helpers ----------------------------------------------------------------

# Path-illegal chars on common filesystems. We keep it conservative so the
# resulting folders work on Linux, NTFS, exFAT, and SMB.
_BAD_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


def safe(name: str) -> str:
    """Return `name` with path-illegal chars replaced and trailing
    dots/spaces stripped. Returns '_' if the result would be empty."""
    cleaned = _BAD_CHARS.sub("_", name).strip().rstrip(". ")
    return cleaned if cleaned else "_"


def read_tags(path: Path) -> dict[str, Optional[str]]:
    """Best-effort tag read. Tries mutagen.File (auto-detects format),
    falls back to format-specific readers for tricky containers. Returns
    a dict with artist/album/title/track_number, all optional."""
    out: dict[str, Optional[str]] = {
        "artist": None, "album": None, "title": None, "track_number": None,
    }
    try:
        # easy=True maps the common cross-format tag names ("artist", "album",
        # ...) to a uniform schema regardless of underlying container.
        f = mutagen.File(str(path), easy=True)
    except Exception as ex:
        log.debug("tag read failed for %s: %s", path, ex)
        return out
    if f is None:
        return out

    def first(key: str) -> Optional[str]:
        v = f.get(key)
        if not v: return None
        if isinstance(v, list): v = v[0] if v else None
        if not v: return None
        return str(v).strip() or None

    # Album-artist takes precedence over track-artist for organization —
    # it keeps multi-artist albums together under one folder.
    out["artist"] = first("albumartist") or first("artist")
    out["album"]  = first("album")
    out["title"]  = first("title")
    track_raw = first("tracknumber")
    if track_raw:
        # Format can be "3" or "3/12". Take the leading integer.
        m = re.match(r"\d+", track_raw)
        if m:
            try: out["track_number"] = int(m.group(0))
            except ValueError: pass
    return out


def parse_filename_fallback(stem: str) -> dict[str, Optional[str]]:
    """If tags are missing/garbage, try to glean artist & title from the
    filename. Common Soulseek patterns:
      'Artist - Title'
      '01 - Title'
      '01 Title'
      'Track 03 - Title'
    Be conservative — return whatever we can confidently extract."""
    s = stem.strip()
    # Drop a leading "01 - " or "01. " or "Track 03 - " etc.
    m = re.match(r"^\s*(?:track\s*)?(\d{1,3})\s*[-.\s]+\s*(.+)$", s, re.IGNORECASE)
    if m:
        s = m.group(2).strip()
    # If "Artist - Title" remains, split on first " - ".
    parts = re.split(r"\s+-\s+", s, maxsplit=1)
    if len(parts) == 2:
        return {"artist": parts[0].strip() or None, "title": parts[1].strip() or None}
    return {"artist": None, "title": s or None}


def resolve_collision(dest: Path) -> Path:
    """If `dest` exists, suffix the stem with ' (2)', ' (3)', etc. until
    we land on an unused name."""
    if not dest.exists(): return dest
    parent, stem, suffix = dest.parent, dest.stem, dest.suffix
    for i in range(2, 100):
        candidate = parent / f"{stem} ({i}){suffix}"
        if not candidate.exists(): return candidate
    return dest  # pathological — overwrite as last resort


def organize_one(path: Path) -> bool:
    """Move a single audio file to its tag-based destination. Returns
    True if the file was actually moved (or is already in the right
    place), False if we couldn't determine where it should go."""
    if path.suffix.lower() not in AUDIO_EXTS:
        return False
    if any(path.name.lower().endswith(s) for s in IGNORE_SUFFIXES):
        return False

    tags = read_tags(path)
    if not tags["artist"] or not tags["title"]:
        # Salvage what we can from the filename.
        fallback = parse_filename_fallback(path.stem)
        tags["artist"] = tags["artist"] or fallback["artist"]
        tags["title"]  = tags["title"]  or fallback["title"]

    artist = tags["artist"]
    title  = tags["title"]
    if not artist or not title:
        log.info("skip (no artist/title resolvable): %s", path)
        return False

    # "Singles" is the default bucket for tag-less downloads — keeps them
    # off the artist's root folder where Jellyfin would treat them as
    # cover-art / artist images.
    album = tags["album"] or "Singles"
    track_n = tags["track_number"]
    prefix = f"{track_n:02d} - " if track_n else ""

    dest_dir = MUSIC_ROOT / safe(artist) / safe(album)
    dest_dir.mkdir(parents=True, exist_ok=True)
    # Make every directory we touch world-rwx so Jellyfin (potentially
    # running as a different uid in its own container) can delete tracks
    # from inside via its own UI. Mirror UMASK=000 behavior.
    try:
        os.chmod(dest_dir, 0o777)
        os.chmod(dest_dir.parent, 0o777)  # the {Artist} folder too
    except OSError: pass

    dest = dest_dir / f"{prefix}{safe(title)}{path.suffix.lower()}"
    dest = resolve_collision(dest)

    try:
        shutil.move(str(path), str(dest))
        # File mode 666: rw for everyone. Jellyfin doesn't need exec on
        # audio files, just write+delete via the parent dir's mode (set
        # above). 666 keeps it consistent with linuxserver UMASK=000.
        try:
            os.chmod(dest, 0o666)
        except OSError: pass
        log.info("moved: %s → %s", path, dest)
        return True
    except (OSError, shutil.Error) as ex:
        log.warning("move failed (%s): %s → %s", ex, path, dest)
        return False


def cleanup_empty_dirs(root: Path) -> None:
    """Walk DOWNLOADS_ROOT bottom-up and rmdir any empty directories.
    Keeps the downloads pile tidy after we've relocated all the audio."""
    if not root.exists(): return
    # bottom-up: reverse-sort by depth
    dirs = [p for p in root.rglob("*") if p.is_dir()]
    dirs.sort(key=lambda p: len(p.parts), reverse=True)
    for d in dirs:
        try:
            if not any(d.iterdir()):
                d.rmdir()
                log.debug("rmdir: %s", d)
        except OSError:
            pass


def trigger_jellyfin_scan() -> None:
    """POST /Library/Refresh on the configured Jellyfin server to make the
    newly-moved file visible immediately. Best-effort — silently no-ops
    if not configured, logs at WARNING on transport failure but doesn't
    re-raise (we don't want to abort the watcher on a Jellyfin hiccup)."""
    if not JELLYFIN_URL or not JELLYFIN_API_KEY:
        return
    url = f"{JELLYFIN_URL}/Library/Refresh"
    req = urllib.request.Request(url, method="POST")
    req.add_header("X-Emby-Token", JELLYFIN_API_KEY)
    req.add_header("Content-Length", "0")
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            log.info("jellyfin scan triggered: %s %d", url, resp.status)
    except urllib.error.HTTPError as ex:
        log.warning("jellyfin scan trigger failed: HTTP %d %s", ex.code, ex.reason)
    except Exception as ex:
        log.warning("jellyfin scan trigger failed: %s", ex)


def scan_once() -> None:
    if not DOWNLOADS_ROOT.exists():
        log.warning("DOWNLOADS_ROOT %s does not exist — nothing to scan", DOWNLOADS_ROOT)
        return
    if not MUSIC_ROOT.exists():
        log.error("MUSIC_ROOT %s does not exist — refusing to organize", MUSIC_ROOT)
        return

    moved = 0
    seen = 0
    for path in DOWNLOADS_ROOT.rglob("*"):
        if not path.is_file(): continue
        if path.suffix.lower() not in AUDIO_EXTS: continue
        seen += 1
        try:
            if organize_one(path):
                moved += 1
        except Exception as ex:
            log.exception("organize_one threw: %s", ex)
    if seen > 0 or moved > 0:
        log.info("scan complete: %d audio files seen, %d moved", seen, moved)
    cleanup_empty_dirs(DOWNLOADS_ROOT)
    # Only ping Jellyfin if we actually moved something this round.
    if moved > 0:
        trigger_jellyfin_scan()


def main() -> int:
    # World-writeable defaults for any new file/dir we create. Without
    # this, Jellyfin (running as a different container uid) can't delete
    # tracks the organizer placed in the music library via its own UI.
    os.umask(0o000)
    log.info("slskd-organizer starting: DOWNLOADS=%s MUSIC=%s POLL=%ds",
             DOWNLOADS_ROOT, MUSIC_ROOT, POLL_INTERVAL)
    while True:
        try:
            scan_once()
        except KeyboardInterrupt:
            return 0
        except Exception as ex:
            log.exception("scan loop error: %s", ex)
        time.sleep(POLL_INTERVAL)


if __name__ == "__main__":
    sys.exit(main() or 0)
