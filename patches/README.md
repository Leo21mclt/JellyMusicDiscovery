# Patches

Third-party fixes the rest of the stack depends on. None of these are part of
the Jellyfin plugin itself — they patch other tools that the plugin's
download pipeline relies on.

## `soularr-version-check.diff`

**What it patches:** [Soularr](https://github.com/mrusse/soularr)'s
`slskd_version_check()` in `/app/soularr.py`.

**Why you need it:** Soularr branches its slskd API calls on the slskd
version string. The parser assumes strict semver; if your slskd reports a
version like `0.0.1.65534-local` (any self-built or `-local`-tagged
build does), the parse raises and Soularr falls back to the OLD API
shape against a NEW-API server. Symptom: `TypeError: directory['files']`
on every Soularr cycle, 0 imports.

**Symptom you'll see in `docker logs soularr`:**

```
TypeError: list indices must be integers or slices, not str
  ...
  File "/app/soularr.py", line ..., in <something>
    files = directory['files']
```

**Fix:** force the new-API path. Safe if your slskd ≥ 0.22.2 (released
mid-2024).

**Apply (one-liner, no rebuild):**

```bash
docker exec soularr sed -i \
  's|return version_tuple >= target_tuple|return True  # forced new-API path|' \
  /app/soularr.py
docker restart soularr
```

If your Soularr container has `/app/soularr.py` bind-mounted to a host path,
edit there and the change survives container recreate.
