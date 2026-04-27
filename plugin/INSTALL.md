# Installing the Jellyfin plugin

The plugin is a drop-in install: extract `JellyMusicDiscovery.zip` into
Jellyfin's plugins folder, restart Jellyfin, then configure under
**Dashboard → Plugins → Music Discovery**.

## 1. Find your Jellyfin plugins folder

| Install type | Path |
|---|---|
| Linux (apt) | `/var/lib/jellyfin/plugins` |
| Linux (docker, linuxserver image) | `<your-config-volume>/data/plugins` |
| Linux (docker, official image) | `<your-config-volume>/plugins` |
| macOS | `~/.local/share/jellyfin/plugins` |
| Windows | `%LOCALAPPDATA%\jellyfin\plugins` |

For docker installs, "your-config-volume" is whatever the host path is in your
compose file's `volumes:` line that maps to `/config` inside the container.

## 2. Extract the zip

Create a folder named `MusicDiscovery_0.1.0.0` (the trailing version must
match `meta.json`'s `version` field) inside the plugins folder, then unzip
into it:

```bash
PLUGINS=/path/to/jellyfin/plugins   # adjust per the table above
mkdir -p "$PLUGINS/MusicDiscovery_0.1.0.0"
unzip JellyMusicDiscovery.zip -d "$PLUGINS/MusicDiscovery_0.1.0.0"
```

After extraction the folder should contain:

```
MusicDiscovery_0.1.0.0/
├── JellyMusicDiscovery.dll
├── TagLibSharp.dll
└── meta.json
```

**Both DLLs are required.** `TagLibSharp.dll` is what the plugin uses to
read audio tags off completed downloads — Jellyfin doesn't bundle it, so
the plugin will throw `FileNotFoundException` at startup if it's missing.

## 3. Restart Jellyfin

```bash
# Docker:
docker restart jellyfin
# systemd:
sudo systemctl restart jellyfin
```

In Jellyfin's web UI, **Dashboard → Plugins** should now list "Music
Discovery" with status **Active**. Click into it to configure.

## 4. Configure

The plugin works without any config — search injection (Deezer + iTunes +
MusicBrainz) is on by default and has no auth requirements. The other
fields are only needed if you want the matching companion services.

| Field | When you need it |
|---|---|
| `MusicLibraryRoot` | Inside Jellyfin's filesystem, where your music lives. Default `/music`. |
| `LidarrBaseUrl` + `LidarrApiKey` | Auto-add albums to Lidarr when users favorite a discovery result. |
| `SlskdBaseUrl` + `SlskdApiKey` | Search slskd directly from the plugin (mostly used for the fallback path; Soularr handles the main download flow). |
| `YtMusicStreamServerUrl` | Instant track-play streaming via the bundled `ytmusic-stream-server`. |
| `EnableSlskdAutoOrganize` | **Leave OFF** if you're using the standalone `slskd-organizer` container (recommended). Only turn on if Jellyfin's container has the slskd downloads dir mounted. |

See the top-level `README.md` for the full architecture and the
`/slskd-organizer` and `/ytmusic-stream-server` directories for those
companion services.

## Uninstall

Remove the `MusicDiscovery_0.1.0.0` folder from the plugins directory and
restart Jellyfin. No DB rows are left behind — the plugin stores its state
inside Jellyfin's config db, which Jellyfin garbage-collects.

## Build it yourself

The full source is in `plugin/source/`. Requires .NET 9 SDK:

```bash
cd plugin/source
make package
# Produces ../artifacts/JellyMusicDiscovery.zip — same shape as this one.
```
