# IntroDB Online Writer (Jellyfin 12.2) — MVP 0.1.0

**Status: source prototype; NOT YET COMPILED or tested on a running Jellyfin 12.2 server.**

This is the first phase of the restart-free Media Segment integration, **not** the on-demand playback lookup plugin yet. It accepts already validated Jellyfin episode timestamp JSONL data and writes it via `IMediaSegmentManager.CreateSegmentAsync` while Jellyfin remains online. It does not access IPTV streams, TheIntroDB, IntroDB.app, or ffmpeg.

## Security and invariants

- Admin-only API (`[Authorize(Policy = Policies.RequiresElevation)]`). Never expose this route unauthenticated.
- Import strictly limited to TV `Episode` objects already in the destination Jellyfin library.
- Only `Intro`, `Outro`, and `Recap` types; valid tick boundaries; known runtime bounded with 3 seconds tolerance; 8-hour maximum without runtime.
- Reject overlapping intro/outro segments; reject duplicate ranges and duplicate episode records in one batch.
- Never deletes or overwrites existing episodes' segments, regardless of provider. Each batch has **at most 100 episodes / 500 segments / 2 MB request body**.
- Registers its own stable provider identity: `IntroDB Online Writer` rather than impersonating TheIntroDB. The no-op provider returns `Supports=false` so scheduled segment scans should not fetch or overwrite its records.
- Dry run is the default in the Python uploader. Explicit `--apply` is required.
- **Not atomic across a whole batch**: Jellyfin's `CreateSegmentAsync` persists rows individually. On a handled per-episode insertion error the plugin attempts to delete segments created in that episode; a process crash may still leave partial records. The next retry skips an episode with any segments, so inspect interrupted imports rather than assuming they completed.
- API keys in environment, never in command line parameters or JSONL.
- Check for pending previously-created segments through the native API before starting a large batch.

## Build — recommended GitHub Actions

Jellyfin **12.2.0 requires .NET 10**. The build workflow is already included at `.github/workflows/build.yml`, using .NET SDK 10 and `Jellyfin.Controller`/`Jellyfin.Model` 12.2.0.

1. Create a private or public GitHub repository for this **source directory**, including the hidden `.github/workflows` folder. Do not upload API keys or your Jellyfin DB.
2. Push all files, then visit **Actions → Build Jellyfin 12.2 plugin → Run workflow**. If Actions are disabled, enable them in repository Settings.
3. The build must turn green. Download the Actions artifact `IntroDBOnlineWriter-jf12.2`; it contains `IntroDBOnlineWriter_0.1.0.zip`.
4. If the workflow fails to compile, **do not install**. Send the C# compiler error output for correction. This source is prepared against documented 12.x interfaces but could not be compiled in the authoring environment.

Alternative build on a machine with .NET SDK 10:

```bash
dotnet publish Jellyfin.Plugin.IntroDBOnlineWriter/Jellyfin.Plugin.IntroDBOnlineWriter.csproj \
  --configuration Release --output ./dist
```

Do **not** copy NuGet Jellyfin DLLs to the server plugin directory. Only copy `Jellyfin.Plugin.IntroDBOnlineWriter.dll` (and optional matching PDB).

## Install only on `jfin-test` first

A ONE-TIME server restart is required to load a newly installed plugin. **Subsequent timestamp imports do not restart Jellyfin.**

1. Check target server Jellyfin **12.2.0** (`Dashboard → System Information`). A different 12.x minor version may need rebuilt dependencies.
2. Verify a restorable snapshot/backup exists.
3. Transfer the built DLL to `jfin-test` and create a dedicated plugin folder (adjust if Jellyfin plugins are stored elsewhere):

```bash
mkdir -p /var/lib/jellyfin/plugins/IntroDBOnlineWriter_0.1.0
install -m 644 Jellyfin.Plugin.IntroDBOnlineWriter.dll \
  /var/lib/jellyfin/plugins/IntroDBOnlineWriter_0.1.0/
systemctl restart jellyfin
journalctl -u jellyfin --since '3 minutes ago' --no-pager | grep -Ei 'IntroDB Online|OnlineWriter|error'
```

Do not remove the existing official TheIntroDB plugin. Keep native Media Segment provider enabled for TV libraries. This plugin registers under a **new** provider name: `IntroDB Online Writer`.

## Authenticate and smoke test

Run these commands on **jfin-test** (not the main `jfin` LXC):

```bash
cd /root/introdb-import
read -rsp 'Jellyfin admin API key: ' JELLYFIN_API_KEY; echo
export JELLYFIN_API_KEY
export JELLYFIN_URL='http://127.0.0.1:8096'
curl -fsS -H "Authorization: MediaBrowser Token=\"$JELLYFIN_API_KEY\"" \
  "$JELLYFIN_URL/Plugins/IntroDBOnlineWriter/Status" | python3 -m json.tool
```

Expect version 0.1.0, provider name and max limits. `401` means invalid key; `403` indicates insufficient administrator role; `404` suggests the controller or plugin isn't loaded (check startup logs).

Copy `tools/upload_segments_online.py` from this package into `/root/introdb-import/`.

Only use a **known validated** `*_checked.jsonl` from **the same Jellyfin instance**. If copying a JSONL from main to test, the Jellyfin `item_id` mappings may differ; regenerate or remap it first. The sample file in this package is illustrative and should NOT be uploaded without checking the item ID.

```bash
python3 upload_segments_online.py --input /root/introdb-import/test_checked.jsonl \
  --max-items 5 --batch-size 5
```

This performs a read-only preflight. If it succeeds, explicitly apply:

```bash
python3 upload_segments_online.py --input /root/introdb-import/test_checked.jsonl \
  --max-items 5 --batch-size 5 --apply
```

Now verify one episode using the Jellyfin native endpoint:

```bash
ITEM_ID='PASTE_EPISODE_ID_FROM_YOUR_CHECKED_FILE'
curl -fsS -H "Authorization: MediaBrowser Token=\"$JELLYFIN_API_KEY\"" \
  "$JELLYFIN_URL/MediaSegments/$ITEM_ID" | python3 -m json.tool
systemctl is-active jellyfin   # should remain active
```

Play the episode on a compatible client. A successful API import only proves server visibility, not seek accuracy.

## Rollback

If any unexpected results occur, stop using the uploader. You can disable/remove the newly added plugin directory and restart Jellyfin; do not delete the existing TheIntroDB plugin or your old imported records. The plugin has **no destructive endpoint** by design. The new provider's records will typically become hidden if the provider isn't registered, but remain in the database — preserve your Proxmox backup. Do not delete all segments for an item; other providers might also own data.

## Planned follow-up (not in v0.1.0)

- Compare TheIntroDB and IntroDB.app on a sample, keep release-aware source prioritization.
- Add playback-start event listener and an episode/season prefetch queue, with negative cache and per-account quota policy.
- Add import status/progress endpoint, safe per-episode partial-import recovery and provider-specific deletion.
- Connect the existing `watchlist_queue.py` and reusable cache to this online writer.
- Validate Jellyfin client skip timing for first play; do not promise first-play intro skip on a playback-start trigger.
