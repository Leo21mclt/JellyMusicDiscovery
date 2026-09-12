"""
ytmusic-stream-server — tiny FastAPI service that bridges Jellyfin Music
Discovery to YouTube Music for on-demand audio streaming.

Endpoints:
  GET /health              -> {ok:true}
  GET /search?artist=&title=
                           -> {videoId, title, artist, durationSeconds, thumbnail}
  GET /stream?artist=&title=
                           -> 302 redirect to a direct audio URL (or 502 on failure)
  GET /stream/by-video/{videoId}
                           -> 302 redirect to a direct audio URL for that video

Notes:
  - YouTube audio URLs are signed and short-lived (~6 hours), but the redirect
    is ad-hoc so each play resolves a fresh URL.
  - We prefer YouTube Music search results because they're audio-first.
"""

from __future__ import annotations
import asyncio
import logging
import os
import time
from typing import Optional

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.responses import RedirectResponse, JSONResponse, StreamingResponse, Response
from ytmusicapi import YTMusic
import httpx
import yt_dlp

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
log = logging.getLogger("ytmusic-stream-server")

app = FastAPI(title="ytmusic-stream-server", version="0.1.0")

# A single shared YTMusic client — instantiation is non-trivial.
_yt: Optional[YTMusic] = None
def yt() -> YTMusic:
    global _yt
    if _yt is None:
        _yt = YTMusic()
    return _yt

# yt-dlp config — pull the best audio-only stream URL, no download.
# Leaving player_client at default lets yt-dlp pick whichever YT client surface
# exposes audio-only formats. The CLI default works; explicit overrides hide
# audio-only formats on some client surfaces.
# Prefer m4a (AAC) over webm (Opus) — Safari/iOS only reliably plays m4a/aac.
# Other browsers (Chrome/Firefox) support both, so this preference order works
# universally. Final fallback to "best" handles videos with weird format sets.
YDL_OPTS = {
    "cookiefile": "/tmp/cookies.txt",
    "format": "bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/bestaudio/best",
    "quiet": True,
    "no_warnings": True,
    "skip_download": True,
    "noplaylist": True,
}

# Video selector — prefer high-quality H.264 DASH up to 1080p. yt-dlp
# returns two URLs (video + audio) for DASH; we mux them with ffmpeg
# `-c copy` (no re-encode, just container repack) to a single MP4 that
# serves seekably from disk.
#
# Format priority:
#   bestvideo[h264, <=1080] + bestaudio[aac]   — DASH 1080p, our target
#   22                                          — legacy 720p progressive (rare)
#   18                                          — 360p progressive fallback
#
# We ALWAYS pick avc1/H.264 over VP9 because iOS Safari refuses VP9 and
# our muxer-copy can't transcode codecs without burning Pi CPU.
YDL_VIDEO_OPTS = {
    "cookiefile": "/tmp/cookies.txt",
    "format": (
        "bestvideo[height<=1080][vcodec^=avc1]+bestaudio[acodec^=mp4a]/"
        "bestvideo[height<=720][vcodec^=avc1]+bestaudio[acodec^=mp4a]/"
        "22/18/best[ext=mp4]"
    ),
    "quiet": True,
    "no_warnings": True,
    "skip_download": True,
    "noplaylist": True,
}

_url_cache: dict[str, tuple[float, str]] = {}
# Note: video URLs aren't cached in-memory anymore — we cache the muxed
# MP4 file on disk instead, which gives both URL persistence and fast
# subsequent plays in one mechanism.

def _expire_from_yt_url(url: str) -> Optional[int]:
    """YouTube embeds an `expire=<unix>` query param in signed audio URLs."""
    import urllib.parse as up
    try:
        q = up.urlparse(url).query
        for kv in q.split("&"):
            if kv.startswith("expire="):
                return int(kv.split("=", 1)[1])
    except Exception:
        pass
    return None


def get_audio_url(video_id: str, force: bool = False) -> Optional[str]:
    """Resolve a YouTube videoId to a direct audio stream URL via yt-dlp."""
    now = time.time()
    if not force:
        cached = _url_cache.get(video_id)
        # Cache hit only if the URL still has a comfortable lifetime.
        # YouTube signed URLs are ~6h but they can be invalidated earlier; we
        # honor the embedded expire and pad 5 minutes for clock skew.
        if cached and cached[0] > now + 300:
            return cached[1]
    url = f"https://music.youtube.com/watch?v={video_id}"
    try:
        with yt_dlp.YoutubeDL(YDL_OPTS) as ydl:
            info = ydl.extract_info(url, download=False)
        audio_url = info.get("url") if info else None
        if not audio_url and info and info.get("requested_formats"):
            audio_url = info["requested_formats"][0].get("url")
        if audio_url:
            expire = _expire_from_yt_url(audio_url) or (int(now) + 60 * 30)
            _url_cache[video_id] = (expire, audio_url)
        return audio_url
    except Exception as ex:
        log.warning("yt-dlp failed for %s: %s", video_id, ex)
        return None


def get_video_urls(video_id: str) -> Optional[tuple[str, Optional[str]]]:
    """Resolve a YouTube videoId to (video_url, audio_url_or_None) via
    yt-dlp. For DASH formats (the 720p+ path) we get two URLs and mux
    them server-side. For progressive formats (the 22/18 fallback) only
    the video URL is set — it already contains audio."""
    url = f"https://www.youtube.com/watch?v={video_id}"
    try:
        with yt_dlp.YoutubeDL(YDL_VIDEO_OPTS) as ydl:
            info = ydl.extract_info(url, download=False)
        if not info:
            return None
        # DASH path: yt-dlp picked separate streams.
        rf = info.get("requested_formats")
        if rf and len(rf) >= 2:
            v_url = rf[0].get("url")
            a_url = rf[1].get("url")
            if v_url and a_url:
                return (v_url, a_url)
        # Progressive single-URL path.
        single = info.get("url")
        if single:
            return (single, None)
        return None
    except Exception as ex:
        log.warning("yt-dlp video failed for %s: %s", video_id, ex)
        return None


def find_artist_videos(artist: str, limit: int = 20) -> list:
    """
    Search YouTube Music for music videos by an artist. Returns a list of
    {videoId, title, artist, thumbnail, durationSeconds} dicts, scored to
    prefer official music videos (artist name match, "Official" in title).
    """
    if not artist:
        return []
    try:
        results = yt().search(artist, filter="videos", limit=max(limit * 3, 30))
    except Exception as ex:
        log.warning("ytmusicapi videos search failed: %s", ex)
        return []
    if not results:
        return []

    artist_l = artist.lower()

    def score(r: dict) -> int:
        s = 0
        rt = (r.get("title") or "").lower()
        # Boost the things that look like real official MVs over random
        # uploads / lyric videos / cover bands.
        if "official" in rt: s += 30
        if any(k in rt for k in ("music video", "official video", "official mv")): s += 20
        if "lyric" in rt: s -= 10
        if any(k in rt for k in ("cover", "remake", "tribute", "karaoke")): s -= 30
        for a in r.get("artists") or []:
            an = (a.get("name") or "").lower()
            if an == artist_l: s += 50
            elif artist_l and artist_l in an: s += 20
        return s

    results.sort(key=score, reverse=True)
    out = []
    for r in results[:limit]:
        vid = r.get("videoId")
        if not vid:
            continue
        out.append({
            "videoId": vid,
            "title": r.get("title"),
            "artist": ", ".join(a.get("name") for a in (r.get("artists") or []) if a.get("name")) or artist,
            "thumbnail": _normalize_thumbnail(r),
            "durationSeconds": r.get("duration_seconds"),
        })
    return out


def best_match(artist: str, title: str) -> Optional[dict]:
    """Search YT Music for `artist - title` and pick the best song result."""
    if not title:
        return None
    query = f"{artist} {title}".strip() if artist else title
    try:
        results = yt().search(query, filter="songs", limit=8)
    except Exception as ex:
        log.warning("ytmusicapi search failed: %s", ex)
        return None
    if not results:
        return None

    title_l = title.lower()
    artist_l = (artist or "").lower()

    def score(r: dict) -> int:
        s = 0
        rt = (r.get("title") or "").lower()
        if rt == title_l: s += 50
        if title_l in rt: s += 20
        for a in r.get("artists") or []:
            an = (a.get("name") or "").lower()
            if artist_l and an == artist_l: s += 40
            elif artist_l and artist_l in an: s += 15
        return s

    results.sort(key=score, reverse=True)
    top = results[0]
    return {
        "videoId": top.get("videoId"),
        "title": top.get("title"),
        "artist": ", ".join(a.get("name") for a in (top.get("artists") or []) if a.get("name")),
        "durationSeconds": top.get("duration_seconds"),
        "thumbnail": (top.get("thumbnails") or [{}])[-1].get("url"),
    }


@app.get("/health")
def health():
    return {"ok": True, "service": "ytmusic-stream-server", "version": "0.1.0"}


@app.get("/search")
def search(artist: str = Query(""), title: str = Query(...)):
    match = best_match(artist, title)
    if not match or not match.get("videoId"):
        raise HTTPException(status_code=404, detail="No match found.")
    return match


async def _proxy_audio_with_retry(video_id: str, request: Request):
    """
    Fetch the upstream audio URL and stream it. If upstream returns 403/410
    (URL expired, peer rejected, etc.) we re-resolve the videoId once and
    retry — handles the "URL was cached but YouTube already invalidated it"
    case that surfaces as random MediaDecodeError on subsequent plays.
    """
    for attempt in (0, 1):
        force = attempt == 1
        audio_url = get_audio_url(video_id, force=force)
        if not audio_url:
            raise HTTPException(status_code=502, detail="yt-dlp could not resolve audio URL.")
        result = await _proxy_audio(audio_url, request)
        if result.status_code in (403, 410, 502, 504) and attempt == 0:
            log.info("upstream returned %d on first attempt, refreshing URL", result.status_code)
            continue
        return result
    raise HTTPException(status_code=502, detail="upstream rejected even after URL refresh")


CACHE_DIR = "/tmp/ytmusic-cache"
os.makedirs(CACHE_DIR, exist_ok=True)


def _cache_path_for(audio_url: str) -> str:
    """Stable filename per upstream URL. yt-dlp's signed URLs include an
    `id=` parameter that survives expiration changes; falls back to a hash."""
    import hashlib, urllib.parse as up
    try:
        q = up.parse_qs(up.urlparse(audio_url).query)
        vid = q.get("id", [None])[0]
        if vid:
            return os.path.join(CACHE_DIR, f"{vid}.mp3")
    except Exception:
        pass
    digest = hashlib.sha256(audio_url.encode()).hexdigest()[:16]
    return os.path.join(CACHE_DIR, f"{digest}.mp3")


async def _transcode_to_file(audio_url: str, out_path: str) -> bool:
    """ffmpeg → MP3 → out_path. Returns True on success."""
    tmp_path = out_path + ".part"
    cmd = [
        "ffmpeg",
        "-hide_banner",
        "-loglevel", "warning",
        "-i", audio_url,
        "-vn",
        "-c:a", "libmp3lame",
        "-b:a", "192k",
        "-f", "mp3",
        "-y", tmp_path,
    ]
    proc = await asyncio.create_subprocess_exec(
        *cmd,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )
    _, stderr = await proc.communicate()
    if proc.returncode != 0:
        log.warning("ffmpeg failed (%s): %s", proc.returncode, stderr.decode(errors="replace")[:500])
        try:
            os.remove(tmp_path)
        except OSError:
            pass
        return False
    try:
        os.replace(tmp_path, out_path)
    except OSError as ex:
        log.warning("rename failed: %s", ex)
        return False
    return True


async def _proxy_audio(audio_url: str, request: Request):
    """
    Stream YouTube's audio to the client RE-ENCODED as MP3.

    Why re-encode instead of just forwarding bytes? YouTube serves audio as
    fragmented MP4 (DASH) — audio engines treat that as an open-ended
    stream and never fire "ended". MP3 is self-contained, ends cleanly.

    Why buffer to disk first instead of streaming the ffmpeg pipe directly?
    Native Jellyfin clients (iOS app, macOS app) and strict browsers
    (Safari) validate audio responses for proper Content-Length and
    Accept-Ranges:bytes. Without those, the audio element rejects the
    stream with "media not supported" — which Finer's permissive AVPlayer
    happens to overlook. Caching to a temp file gives us:
      - Exact Content-Length (file size on disk)
      - Real Range support (FastAPI's FileResponse handles it)
      - Subsequent plays of the same track are instant (cache hit)
    First play has a ~3-5s transcode delay; cached plays start in <100ms.
    """
    cache_file = _cache_path_for(audio_url)

    if not os.path.exists(cache_file):
        log.info("cache miss, transcoding to %s", cache_file)
        ok = await _transcode_to_file(audio_url, cache_file)
        if not ok or not os.path.exists(cache_file):
            raise HTTPException(status_code=502, detail="Transcode failed.")
    else:
        log.debug("cache hit %s", cache_file)

    # FastAPI's FileResponse handles Range requests, sets Content-Length,
    # Accept-Ranges, and Last-Modified for us. Browsers/native clients see
    # a normal seekable file response.
    from fastapi.responses import FileResponse
    return FileResponse(cache_file, media_type="audio/mpeg", filename=os.path.basename(cache_file))


@app.api_route("/stream", methods=["GET", "HEAD"])
async def stream(request: Request, artist: str = Query(""), title: str = Query(...)):
    match = best_match(artist, title)
    if not match or not match.get("videoId"):
        raise HTTPException(status_code=404, detail="No YT Music match.")
    if request.method == "HEAD":
        # Quick HEAD on upstream so client gets content-type / content-length.
        audio_url = get_audio_url(match["videoId"])
        if not audio_url:
            raise HTTPException(status_code=502, detail="yt-dlp could not resolve audio URL.")
        async with httpx.AsyncClient(timeout=10.0, follow_redirects=True) as client:
            upstream = await client.head(audio_url, headers={"User-Agent": "Mozilla/5.0"})
        out = {}
        for h in ("content-type", "content-length", "accept-ranges"):
            v = upstream.headers.get(h)
            if v: out[h] = v
        if "accept-ranges" not in out:
            out["accept-ranges"] = "bytes"
        return Response(status_code=200, headers=out)

    return await _proxy_audio_with_retry(match["videoId"], request)


@app.api_route("/stream/by-video/{video_id}", methods=["GET", "HEAD"])
async def stream_by_video(video_id: str, request: Request):
    if request.method == "HEAD":
        return Response(status_code=200, headers={"accept-ranges": "bytes"})
    return await _proxy_audio_with_retry(video_id, request)


# =====================================================================
# /search/videos — search YouTube Music for an artist's videos.
# Used by the Jellyfin plugin to populate per-artist Music Video tabs.
# =====================================================================
@app.get("/search/videos")
def search_videos(artist: str = Query(..., min_length=1), limit: int = Query(20, ge=1, le=50)):
    videos = find_artist_videos(artist, limit=limit)
    return {"artist": artist, "count": len(videos), "videos": videos}


# =====================================================================
# /video — resolve an artist+title to a YouTube music-video URL and
# 302-redirect the client to YouTube's CDN. No proxying through the Pi
# — saves bandwidth, and YouTube's edge caches handle range requests
# better than we ever could. The signed URL is good for ~6h, which is
# fine since the player connects once at start of playback.
# =====================================================================
@app.api_route("/video", methods=["GET", "HEAD"])
async def video(request: Request, artist: str = Query(""), title: str = Query(...)):
    match = best_match(artist, title)
    if not match or not match.get("videoId"):
        # Fallback: search videos directly (catches things YT Music's "songs"
        # filter excludes — concert footage, vlogs that are still music-video-
        # shaped uploads, etc.)
        videos = find_artist_videos(f"{artist} {title}", limit=1)
        if not videos:
            raise HTTPException(status_code=404, detail="no video match")
        video_id = videos[0].get("videoId")
    else:
        video_id = match["videoId"]
    return await _video_redirect(video_id, request)


@app.api_route("/video/by-id/{video_id}", methods=["GET", "HEAD"])
async def video_by_id(video_id: str, request: Request):
    return await _video_redirect(video_id, request)


def _video_cache_path(video_id: str) -> str:
    return os.path.join(CACHE_DIR, f"video-{video_id}.mp4")


async def _video_redirect(video_id: str, request: Request):
    """
    Stream a fragmented MP4 directly from ffmpeg's stdout. ffmpeg copies
    DASH video+audio streams (no re-encode) into fragmented MP4 fragments
    that browsers can play as they arrive. First-byte latency is ~1-3s
    rather than the ~2 minutes a full-disk-mux would take to complete.

    Trade-offs vs. the disk-cache approach:
      ✗ no seek bar — fragmented MP4 served as a stream isn't seekable in
        most browsers (the moov is at the start but byte ranges into a
        live producer don't work)
      ✗ no cache — subsequent plays re-mux from scratch
      ✓ playback starts almost immediately

    `frag_keyframe+empty_moov` makes ffmpeg write a streamable MP4 (moov
    upfront, video data in fragments) instead of the default "moov at end"
    layout that forces a full-file write before the client sees any bytes.
    """
    if request.method == "HEAD":
        return Response(status_code=200, headers={"accept-ranges": "none", "content-type": "video/mp4"})

    urls = get_video_urls(video_id)
    if urls is None:
        raise HTTPException(status_code=502, detail="yt-dlp could not resolve video URL")
    v_url, a_url = urls
    log.info("/video stream vid=%s dash=%s", video_id, a_url is not None)

    cmd = ["ffmpeg", "-hide_banner", "-loglevel", "error"]
    if a_url:
        cmd += ["-i", v_url, "-i", a_url, "-map", "0:v:0", "-map", "1:a:0"]
    else:
        cmd += ["-i", v_url]
    cmd += [
        "-c", "copy",
        "-movflags", "frag_keyframe+empty_moov+default_base_moof",
        "-f", "mp4",
        "pipe:1",
    ]

    proc = await asyncio.create_subprocess_exec(
        *cmd,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )

    async def stream_bytes():
        try:
            while True:
                chunk = await proc.stdout.read(64 * 1024)
                if not chunk:
                    break
                yield chunk
        finally:
            if proc.returncode is None:
                try: proc.kill()
                except ProcessLookupError: pass
            try:
                err = (await proc.stderr.read()).decode(errors="replace")[:500]
                if err:
                    log.debug("ffmpeg stderr for %s: %s", video_id, err)
            except Exception: pass

    return StreamingResponse(stream_bytes(), media_type="video/mp4",
                             headers={"accept-ranges": "none"})


# =====================================================================
# /discover/playlists — curated playlists for the Jellyfin plugin's
# DiscoveryPlaylistFeed. Cached in memory for ~6 hours; refresh is
# triggered by the plugin via a cache-bust query param.
# =====================================================================

_discover_cache: dict = {"sections": [], "expires": 0.0}
_DISCOVER_TTL_SEC = 6 * 60 * 60


def _normalize_thumbnail(t: dict) -> Optional[str]:
    """Pull the largest thumbnail URL from a YT Music item."""
    thumbs = t.get("thumbnails") or []
    if not thumbs:
        return None
    return thumbs[-1].get("url")


def _normalize_artists(item: dict) -> str:
    """Join YT Music artist list ['name'] entries with comma."""
    artists = item.get("artists") or []
    names = [a.get("name") for a in artists if a.get("name")]
    return ", ".join(names)


def _track_payload(t: dict) -> Optional[dict]:
    """Convert a YT Music track-shaped dict into our discovery-track JSON.
    Wrapped in try/except so a single malformed entry doesn't kill the
    whole section (some chart entries from non-US regions have unusual
    shapes that confuse ytmusicapi's parser)."""
    try:
        vid = t.get("videoId")
        title = t.get("title")
        if not vid or not title:
            return None
        album = t.get("album")
        album_name = album.get("name") if isinstance(album, dict) else None
        return {
            "videoId": vid,
            "title": title,
            "artist": _normalize_artists(t),
            "album": album_name,
            "durationSeconds": t.get("duration_seconds"),
            "thumbnail": _normalize_thumbnail(t),
        }
    except Exception as ex:
        log.debug("_track_payload skipped malformed entry: %s", ex)
        return None


def _normalize_playlist_tracks(playlist: dict, limit: int = 100) -> list:
    out = []
    for t in (playlist.get("tracks") or [])[:limit]:
        p = _track_payload(t)
        if p:
            out.append(p)
    return out


def _normalize_chart_tracks(items: list, limit: int = 50) -> list:
    out = []
    for t in items[:limit]:
        p = _track_payload(t)
        if p:
            out.append(p)
    return out


# Spec: each entry is (section_key, display_title, sub_label, mood_keyword).
# We match mood_keyword case-insensitively against YT Music's mood category
# titles, then take the first playlist in that mood as the section's content.
# Note: YT Music's actual mood-category names (verified via get_mood_categories):
#   Chill, Commute, Energize, Feel good, Focus, Gaming, Party, Romance,
#   Sad, Sleep, Workout, African, Arabic, ..., J-Pop, K-Pop, Pop, Rock, ...
_MOOD_PICKS = [
    ("mood-workout",         "YT 🏋️ Workout",          "Pumped-up YT Music mix",   "workout"),
    ("mood-party",           "YT 🎉 Party",             "Crowd-pleasers",            "party"),
    ("mood-commute",         "YT 🚗 Commute",           "Tunes for the drive",       "commute"),
    # "Energy Boosters" in the user's request → YT Music's category is "Energize".
    ("mood-energy",          "YT ⚡ Energy Boosters",    "High-energy picks",         "energize"),
    ("mood-feelgood",        "YT 😊 Feel Good",          "Upbeat & cheery",           "feel good"),
    # J-Pop is a YT Music mood category, but ytmusicapi 1.8.2 has a parser
    # bug ('musicTwoRowItemRenderer') for genre-mood playlists. Pulled via
    # search instead — see _SEARCH_PICKS below.
]

# Hot Hits genre playlists — fetched via YT Music search for the exact phrase.
# YT Music has dedicated "Hot Hits {genre}" curated playlists that update weekly.
_HOT_HITS = [
    ("hothits-pop",        "YT 🎤 Hot Hits Pop",         "Hot Hits Pop"),
    ("hothits-rock",       "YT 🎸 Hot Hits Rock",        "Hot Hits Rock"),
    ("hothits-emo",        "YT 🖤 Hot Hits Emo",         "Hot Hits Emo"),
    ("hothits-electronic", "YT 🎧 Hot Hits Electronic",  "Hot Hits Electronic"),
]


async def _yt_chart_section(section_key: str, title: str, sub_label: str, country: str, limit: int = 50) -> Optional[dict]:
    """Pull country-specific top songs. JP and other non-US regions sometimes
    return a slightly different shape from the US chart — we walk known
    fallbacks rather than assuming `songs.items`."""
    try:
        charts = await asyncio.to_thread(yt().get_charts, country=country)
    except Exception as ex:
        log.warning("get_charts(country=%s) raised: %s", country, ex)
        return None
    if not charts:
        return None

    # Try several shapes ytmusicapi has used historically.
    candidates = []
    songs = charts.get("songs")
    if isinstance(songs, dict):
        candidates.append(songs.get("items"))
    elif isinstance(songs, list):
        candidates.append(songs)
    # Some regions surface only 'trending' or 'videos' instead of 'songs'.
    for k in ("trending", "videos", "topVideos"):
        v = charts.get(k)
        if isinstance(v, dict):
            candidates.append(v.get("items"))
        elif isinstance(v, list):
            candidates.append(v)

    items = next((c for c in candidates if c), None)
    if not items:
        log.warning("chart %s: no items found in any known shape (keys=%s)",
                    country, list(charts.keys()))
        return None

    tracks = _normalize_chart_tracks(items, limit)
    if not tracks:
        return None
    return {"section": section_key, "title": title, "subLabel": sub_label, "tracks": tracks}


async def _yt_search_playlist_section(section_key: str, title: str, query: str, limit: int = 100) -> Optional[dict]:
    """Search YT Music for a curated playlist by name, return its tracks."""
    try:
        results = await asyncio.to_thread(yt().search, query, filter="playlists", limit=5)
        if not results:
            return None
        # Pick the first playlist result. Some results have browseId, some
        # have playlistId — accept either.
        first = results[0]
        pid = first.get("browseId") or first.get("playlistId")
        if not pid:
            return None
        # browseId comes prefixed with 'VL' for playlists; strip if present.
        if pid.startswith("VL"):
            pid = pid[2:]
        playlist = await asyncio.to_thread(yt().get_playlist, pid, limit)
        tracks = _normalize_playlist_tracks(playlist, limit)
        if not tracks:
            return None
        return {
            "section": section_key,
            "title": title,
            "subLabel": playlist.get("title", query),
            "tracks": tracks,
        }
    except Exception as ex:
        log.warning("search-playlist '%s' failed: %s", query, ex)
        return None


async def _yt_mood_section(section_key: str, title: str, sub_label: str, mood_keyword: str, limit: int = 80) -> Optional[dict]:
    """Find a YT Music mood category whose title contains `mood_keyword`,
    take the first playlist in it, return its tracks."""
    try:
        mood_cats = await asyncio.to_thread(yt().get_mood_categories)
        target_params = None
        target_title = None
        # mood_cats is a dict like {'Moods & moments': [{'title': 'Workout', 'params': '...'}, ...], ...}
        for group_items in mood_cats.values():
            for item in (group_items or []):
                if mood_keyword.lower() in (item.get("title") or "").lower():
                    target_params = item.get("params")
                    target_title = item.get("title")
                    break
            if target_params:
                break
        if not target_params:
            # Help diagnose missing matches by dumping what categories ARE there.
            available = []
            for items in mood_cats.values():
                for it in (items or []):
                    if it.get("title"):
                        available.append(it["title"])
            log.warning("mood %s: no category matched keyword '%s'. Available: %s",
                        section_key, mood_keyword, available)
            return None
        log.info("mood %s: matched '%s'", section_key, target_title)

        mood_pls = await asyncio.to_thread(yt().get_mood_playlists, target_params)
        if not mood_pls:
            return None
        pid = mood_pls[0].get("playlistId")
        if not pid:
            return None
        playlist = await asyncio.to_thread(yt().get_playlist, pid, limit)
        tracks = _normalize_playlist_tracks(playlist, limit)
        if not tracks:
            return None
        return {
            "section": section_key,
            "title": title,
            "subLabel": f"{sub_label} — {mood_pls[0].get('title','')}",
            "tracks": tracks,
        }
    except Exception as ex:
        log.warning("mood %s failed: %s", section_key, ex)
        return None


async def _build_discover() -> dict:
    """Fetch all sections, ignoring failures so partial successes still ship."""
    log.info("[/discover/playlists] building fresh snapshot")

    coros = []
    # Mood-based picks (J-Pop now comes from the mood "J-Pop" category —
    # ytmusicapi 1.8.2's get_charts(country='JP') is broken with
    # "list index out of range").
    for key, title, sub, mood_kw in _MOOD_PICKS:
        coros.append(_yt_mood_section(key, title, sub, mood_kw, 80))
    # 3. Hot Hits genre playlists
    for key, title, query in _HOT_HITS:
        coros.append(_yt_search_playlist_section(key, title, query, 100))
    # 4. Forgotten Favorites
    coros.append(_yt_search_playlist_section(
        "forgotten-favorites", "YT 💭 Forgotten Favorites", "Forgotten Favorites", 100))
    # 5. New Releases
    coros.append(_yt_search_playlist_section(
        "new-releases-yt", "YT 🆕 New Releases", "New Releases", 100))
    # 6. J-Pop (via search since the mood path is broken in ytmusicapi)
    coros.append(_yt_search_playlist_section(
        "jpop-yt", "YT 🇯🇵 J-Pop", "J-Pop Hits", 100))

    results = await asyncio.gather(*coros, return_exceptions=False)
    sections = [r for r in results if r is not None]
    log.info("[/discover/playlists] built %d sections", len(sections))
    return {"sections": sections}


@app.get("/discover/playlists")
async def discover_playlists(refresh: bool = Query(False)):
    now = time.time()
    if refresh or now > _discover_cache["expires"] or not _discover_cache["sections"]:
        snap = await _build_discover()
        _discover_cache["sections"] = snap["sections"]
        _discover_cache["expires"] = now + _DISCOVER_TTL_SEC
    return {"sections": _discover_cache["sections"]}
