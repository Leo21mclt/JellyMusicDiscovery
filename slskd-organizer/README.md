# slskd-organizer

Watches the slskd downloads directory and auto-sorts completed audio
files into the music library by their tags:

```
/portainer/Downloads/peer123/01 - goosebumps.flac
                              ↓ (read tags: artist=Travis Scott, album=Birds in the Trap, title=goosebumps, track=1)
/portainer/Music/Travis Scott/Birds in the Trap/01 - goosebumps.flac
```

## Deploy

```bash
sudo docker compose -f /portainer/Files/AppData/Stacks/slskd-organizer/docker-compose.yml up -d --build
```

Then watch it run:

```bash
sudo docker logs -f slskd-organizer
```

## What it does

- Polls `/data/downloads` (mounted from `/portainer/Downloads`) every 30s
- For each `.mp3 / .flac / .m4a / .ogg / .opus / .wav` file:
  - Reads tags via [mutagen](https://mutagen.readthedocs.io/)
  - Falls back to filename heuristics (`Artist - Title`, `01 - Title`) if tags missing
  - Moves to `/data/music/{Artist}/{Album}/{NN - }Title.{ext}` (where NN is the track number, padded)
  - Sanitizes path components (illegal chars → `_`)
  - Resolves collisions with ` (2)`, ` (3)`, etc.
- Removes empty source directories after moving

## Tuning

Override via env vars in `docker-compose.yml`:
- `POLL_INTERVAL_SEC` — how often to scan (default `30`)
- `DOWNLOADS_ROOT` — source path inside the container (default `/data/downloads`)
- `MUSIC_ROOT` — destination path inside the container (default `/data/music`)
- `JELLYFIN_URL` — Jellyfin base URL (e.g., `http://<your-host-lan-ip>:8096`).
  When set + `JELLYFIN_API_KEY`, the organizer pings `/Library/Refresh`
  after each batch that moved files, so new tracks appear immediately
  instead of waiting for Jellyfin's scheduled scan.
- `JELLYFIN_API_KEY` — create one in Jellyfin admin → Dashboard → API Keys.

## On-demand scan

When `JELLYFIN_URL` and `JELLYFIN_API_KEY` are both set, the organizer
fires exactly ONE `POST /Library/Refresh` per scan cycle that moved
≥1 file (debounced, so a 12-track album that lands all at once triggers
one refresh, not twelve). Without these, Jellyfin picks up new tracks on
its scheduled scan (Dashboard → Scheduled Tasks → "Scan media library").

## When it can't figure out where a file goes

Files without resolvable artist+title (no tags, unparseable filename) are
**left in place**. Logs will show `skip (no artist/title resolvable):
<path>` so you can investigate. Manually moving such files into
`/portainer/Music/` is fine — the watcher won't touch anything that's
already inside `MUSIC_ROOT`.
