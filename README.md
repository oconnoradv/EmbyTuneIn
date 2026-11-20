# TuneIn for Emby

TuneIn adds an audio channel to Emby with administrator-configured browsing,
category refreshes, station counts, and live playback. Opening the settings
page or channel root does not recursively crawl the TuneIn catalog.

## Requirements and installation

Use Emby Server **4.9.1.90+ stable** or a compatible **4.9.2.7+ beta**.
The plugin assembly version is **1.2.8.0**. Release downloads include
`build-info.json` to identify the source commit and `SHA256SUMS.txt` to verify
the files.

The [maintenance RC1 prerelease](https://github.com/oconnoradv/EmbyTuneIn/releases/tag/v1.2.8-maintenance-browsing-refresh-rc.1)
was rebuilt with 48-hour discovery/import deadlines and interruption-only
automatic recovery. Timeouts and manual cancellation do not trigger recovery
retries. The playback-start improvements remain included. Compatible catalog
updates use top-level category-scoped imports; see
[Category-scoped and progressive imports](#category-scoped-and-progressive-imports)
for eligibility and recovery limits.
If you downloaded RC1 before this update, download the new assets: the release
tag and assembly version are unchanged, but the source commit and checksums differ.

1. Stop Emby and back up the existing plugin DLL, TuneIn configuration,
   `TuneIn.refresh.xml` (including `.discovery` and `.recovery` sidecars), and
   `tunein-fork-settings-lock.xml` if present.
   A full server-data backup also preserves library metadata for rollback.
2. Replace `MediaBrowser.Plugins.TuneIn.dll` in the plugins directory.
   Keep only one copy; do not copy dependency DLLs or reset the configuration.
3. Restart Emby and reload its dashboard. If cached settings fields remain
   stale, refresh the affected client's cached page data.

Keep the navigation signing key in the plugin configuration private and retain
it during upgrades. Replacing it invalidates existing item IDs. To roll back,
stop Emby and restore the previous DLL and configuration together, with the
corresponding refresh state. Restoring those files alone does not undo library
metadata changes; a full server-data backup is needed for exact rollback.

## Configure browsing

As an administrator, open **Advanced → TuneIn**, also available from
**Plugins → TuneIn → Settings**. Unlock settings, choose the categories and
selections to expose, enable **configuration browsing after saving**, and **Save**.

| Category | Required configuration |
| --- | --- |
| Music | At least one enabled saved genre selection |
| By Language | At least one enabled saved language selection |
| By Location | At least one enabled saved location selection |
| Local Radio | One source: saved cities/regions or coordinates |
| Talk, Sports, Podcasts | Enable each category independently |
| My Favorites | A TuneIn username |

Local Radio preserves both source configurations but queries only the selected
one. If its source is ambiguous, choose regions or coordinates and save.
Other categories remain independent. Coordinates use decimal latitude and
longitude separated by a comma, within -90..90 and -180..180.

Expand a selector branch to load its choices. **Add**, at the right of a row,
saves a selection without expanding it. Saved paths can be individually enabled
or removed, with up to 100 selections per selector. Choices appear in a
fixed-height scrolling panel. Temporary branch failures retry once; incomplete
branches are marked, and saved choices are preserved.

The **Settings** switch, beside **Global TuneIn Settings**, prevents accidental
edits and saves. The lock persists across browser sessions and server restarts.
**Save** locks settings after a successful save; switch it off to edit again.
Locking does not save pending changes or stop
a refresh. **Station Selections** groups Music Genres, Languages, Locations,
Local Cities and Regions, Talk Topics, Sports & Leagues, and Podcast Topics. Collapsing it keeps unsaved choices; opening it
does not fetch from TuneIn by itself.
Buttons use the active user's native Emby theme.

### Global TuneIn Settings

This section opens before the first successful settings save. After a save it
defaults to collapsed and uses a green tint. Browsing is off on a new install.
Checking **Enable configuration browsing after saving** unlocks the dependent
editors immediately when settings are unlocked, but does not enable catalog
browsing or refresh until you save.

- **Hide the individual genre folders** hides recognized genre shortcuts in
  Local Radio, By Location, Talk, Sports and Podcasts. It keeps provider-supplied
  **By Genre** folders and their contents, stations, popular listings and
  pagination. Music and By Language are unaffected. Refresh the affected
  categories after changing it.
- **Import valid results when catalog entries are skipped** permits partial
  imports of valid results when malformed trusted provider links are skipped.
  See [Partial results](#partial-results).
- **API request interval** sets minimum request spacing from 1 to 30 seconds,
  defaulting to 1 second between request starts when unset. Previously saved
  intervals are preserved. TuneIn retry delays take precedence.

Both checkboxes default to checked when the settings are absent. Explicitly
saved unchecked choices remain unchanged. Changing these options does not
change existing navigation IDs.

Talk, Sports, and Podcasts default to **All**. Choose **Selected only** in the
corresponding selector, then open **Browse** in the category header to load
options from TuneIn. Saved choices stay below the browser controls.
Only selected branches still offered by the provider are included. Empty or
stale selections do not fall back to **All**.

Browsing and playback enforce the saved category and selection scopes,
including nested Music, Talk, and Sports items under By Language. Navigation
IDs are signed and bounded and survive restarts. Scope changes invalidate
affected IDs; return to the channel root after saving. Stale library entries
cannot bypass the current settings.

Configurations without schema version 1 retain their username and coordinates
but do not automatically enable browsing or categories. Save explicit choices
to enable browsing. Legacy URL-based `subcat_`, `preset_`, and `stream_` IDs are
not accepted.

## Refresh the catalog

Save browsing settings before refreshing. In **Catalog Refresh**, select
**Refresh** beside a category or **Refresh all enabled categories**. Opening
the panel reads status without starting a refresh; leaving it stops polling,
not the server-side run.

Discovery finishes before any import begins. When changes require an import,
the plugin supplies Emby with the **complete staged channel tree**, including
saved sibling categories. A single-category refresh fetches only that category.
Unchanged catalogs skip import unless reconciliation is needed.

The import asks Emby to traverse the depth of the staged tree, including
forward pagination folders. The separate discovery limit on nested folders
still applies. If Emby leaves folders unread, the refresh remains incomplete
and the diagnostic report records the served and expected folder counts and
the requested refresh level.

Saving additional enabled categories preserves listings and refresh history for
unchanged scopes. Categories not yet imported remain pending until their first
successful refresh. If a previously imported sibling listing is unavailable or
its scope has changed, refresh it first or use **Refresh all enabled categories**.
A missing or incompatible catalog baseline requires a full refresh.

One refresh runs at a time, with up to two distinct requests waiting.
**Cancel refresh** cancels the active run and clears the queue. Queued requests
do not survive a restart. After a failure, choosing another category offers
**Retry previous first** or **Prioritize new refresh**.

Emby imports are not transactional. An interrupted import may have changed
metadata; the next valid refresh supplies the complete catalog for
reconciliation, even if discovery finds no changes. Prioritizing another
category does not remove that repair requirement.

The TuneIn control panel follows the active Emby web theme. The cleanup warning
keeps a distinct color to identify the irreversible action.

### Schedules

Choose **Off**, **Every 6 hours**, **Every 12 hours**, **Daily**, **Weekly**, or
**Every 30 days** (720 hours) beside a category, then **Save**. Schedules default to off and apply only to
enabled categories. Intervals begin when the plugin detects the setting or
Emby starts; missed runs are not replayed after downtime.

Manual and scheduled runs share the same queue and never overlap. The plugin
owns these schedules; it does not create, edit, or trigger Emby Scheduled Tasks.
Emby's native channel refresh can browse the configured catalog, but its
callbacks are not reported as successful Control Panel imports.

### Partial results

Malformed queries or fragments on trusted TuneIn child links can be skipped
without aborting valid sibling discovery. With **Import valid results when
catalog entries are skipped** enabled, this also permits exclusion-based
imports across categories, including malformed trusted station fallback URLs.

Compatible saved branches are retained where affected folders could not be
fully rediscovered. Saved stations are retained where station fallback URLs
were skipped. Without saved counterparts, rejected entries and their
undiscovered contents are excluded. A rejected link can represent an entire
branch, not just one station. Valid results can be imported without a previous
baseline, but the outcome and saved listing remain **Partial**.

With the option disabled, Music can still apply a retention-only merge when
every affected folder has a compatible saved listing and no rebuild or
interrupted-import repair is needed. Otherwise incomplete discovery leaves the
library unchanged. An inconsistent retained tree also prevents import.

The setting never overrides unsafe destinations, selection or pagination scope
violations, unrecovered request failures, cancellation, timeouts, or catalog
budgets. Changing it applies to the next manual or scheduled refresh; it cannot
apply previously fetched results without another discovery.

### Status and station counts

**Catalog Browsing**, **TuneIn Audio Categories**, and **Station Selections**
collapse after settings are saved and start collapsed on later visits.
**Catalog Refresh** starts expanded and can be collapsed independently; its
heading keeps the current refresh state visible.
Its neutral gray background follows the active Emby theme: light gray on a
light theme and a darker surface on a dark theme. Green marks success without
a bold outline; partial results and errors use stronger orange and red
outlines alongside their status text.
Live metrics show elapsed time, last progress, category progress, and catalog
pages against the safety limit. During import, progress switches to folders
served out of the known total. Discovery does not estimate a percentage or ETA
from the page cap.

Import progress advances only on the first request for each staged folder.
Repeated requests still return the complete listing but do not reset the last
progress time or suppress stall warnings. Folders served measures provider
consumption, not confirmed Emby database commits.

**Diagnostics** shows provider attempts and successes, cached-page replays,
active HTTP requests, rate-limit retries, checkpoint resume, and skipped-link
counts grouped by category and reason. Up to five reason groups are displayed;
the rest are combined into an “other” count. HTTP errors have readable labels,
and recovered rate limits are identified after a completed run.

Each category shows two counts to the left of **Refresh**:

| Count | Meaning |
| --- | --- |
| Available (filtered) | Distinct station IDs found by the latest completed discovery, before saved entries are retained or merged. Partial marks a lower bound. |
| In Emby | Distinct station IDs actually present under the category's Emby folder, checked before discovery and after a verified import. |

Counts are snapshots; hover a value for its check time. Counts saved with a
successful refresh survive restarts. A withheld partial discovery updates the
current dashboard count without replacing the saved catalog. **In Emby** retains
its last successful value and check time when the page reopens or a later
inventory read fails. If a read fails or an import needs reconciliation, the
value is labeled **last known**, with the warning and successful check time in
its tooltip. Unrecorded values say **Not checked**; a failed inventory read
without a previous value says **Unavailable**. A successful check, including
zero stations, replaces the saved value and clears the read warning. Status
polling does not recrawl TuneIn or query the library inventory.

### Diagnostic reports

After a failed or timed-out refresh, or a run with skipped entries or recovered
interruptions, select **Retrieve refresh log**. This administrator-only report
covers the latest discovery and its import attempts, not the full Emby server
log. It is also available for **Partial** outcomes.

Reports include the outcome, grouped skip counts, and up to 100 skipped-entry
examples within a 64 KB report. Examples record the timestamp, category,
rejection reason, source page, and rejected navigation query values and
fragment. Duplicate parameters and truncation are identified. Timeout reports
record the phase that stopped, discovery/import timeout counts and interruption retry limits,
checkpoint time, and whether an import may have partially applied. Live status
identifies whether an automatic retry is for discovery or import.

Non-navigation parameters, including usernames, coordinates, serials, partner
IDs, and tokens, are omitted. Embedded credential-like assignments are
redacted. Entry labels, full provider responses, and signed item IDs are not
captured. Review reports before sharing: exception traces may contain private
server details.

Reports remain in memory until a new refresh, scope reset, or server restart.
Detailed rejected links are not included in routine status polls or refresh
summary logs and cannot be reconstructed from past aggregate skip counts.

### Clean up the TuneIn library

Administrators can open **Maintenance → Clean Up Library Folders** below **Catalog Refresh**.
Cleanup cannot be undone. **Backup Emby** requests the normal **Emby Server Backup**
scheduled task. It requires the [Backup & Restore plugin and Emby Premiere](https://emby.media/support/articles/Backup-Using-Plugin.html)
and uses your existing backup settings. TuneIn requires exactly one scheduled
task named **Emby Server Backup**. A request is not a completed backup: wait for
Emby to report success, or verify another backup separately if the task is unavailable.

Select **Preview cleanup** to count generated folders and station
entries verified under the TuneIn channel. Acknowledge that you verified a
successful backup, then confirm within five minutes to remove those entries in
background batches. Unknown or unsigned entries are
left alone; a folder with remaining children is not deleted. The channel root,
settings and media files are not cleanup targets. Playlists and other
references to removed entries may break.

Refreshes (including schedules and recovery) and settings writes cannot run
while cleanup is active. The dashboard reports progress and partial failures.
Before removing entries, cleanup clears refresh checkpoints and counts so an
interruption cannot cause old reconciliation data to be replayed. Afterwards,
select **Refresh all enabled categories** to rebuild the baseline.

### Request limits and recovery

| Limit | Behavior |
| --- | --- |
| Discovery attempt | Up to 48 hours, independent of import time |
| Import attempt | A fresh 48 hours for staging, importing and verification/finalization |
| Catalog cache | Up to 5,000 pages and 40 MiB per refresh |
| Catalog HTTP response | 90 seconds including body reads, excluding pacing and provider cooldowns |
| Discovery checkpoint | At the first completed folder after each 20-minute interval, and on discovery interruption or timeout when possible |
| Automatic refresh retries | Up to 3 for interrupted work within an active refresh, shared across discovery and import |
| Timeout or manual Cancel | Drains active work and stops; never triggers an automatic recovery retry |
| Checkpoint resume window | 48 hours from discovery start, with matching settings and requested category scope |
| Folder depth | Eight levels; forward pagination does not consume depth but still consumes page and folder budgets |

Discovery can overlap two independent requests without reducing shared spacing
between request starts. HTTP 429 responses have a run-wide budget of three
retries, with consecutive minimum cooldowns of 30, 60, and 120 seconds. Longer
`Retry-After` delays take precedence. Rate limiting doubles the effective
catalog interval up to 30 seconds and temporarily returns discovery to serial
requests. Every 20 consecutive successful responses restores overlap and
reduces the learned interval by one second toward the configured minimum.
Playback startup has its own queue and one in-flight TuneIn API opening slot.
It can start alongside discovery without preempting in-flight work. When
both queues can start, at most two playback requests go ahead of a waiting
catalog request. Both queues still share API request spacing and `Retry-After`.

A discovery timeout saves additional validated pages when possible, then reports
**TimedOut** without retrying. An import deadline also cancels and drains its
attempt, then stops without retrying. The separate 90-second catalog HTTP
response timeout is unchanged and likewise does not trigger a refresh retry.
Checkpoints remain available for a manual refresh until import preparation
succeeds. Timeouts never publish incomplete trees.

Unexpected cancellation of an active operation (without a refresh cancellation
request), transport I/O failures, and provider HTTP 5xx failures other than 504
can trigger a bounded interruption retry, after the failed operation drains.
Invalid data, unsupported/incomplete trees, other provider HTTP errors, saved
scope changes, and manual **Cancel refresh** do not trigger recovery retries.
Provider rate-limit request retries remain a separate policy.

The small `TuneIn.refresh.xml.recovery` sidecar records the active request and
retry count. Starting or restarting Emby never starts a refresh, even if a
refresh was interrupted. Only a manual request or a due configured schedule can
start new work; compatible discovery checkpoints and required reconciliation
remain available to that run. Queued requests are not recovered.

Each recovery attempt has fresh deadlines. Import retries reuse the complete
staged catalog, including saved siblings and any Partial outcome, only while
it remains in memory, compatible with saved settings, and less than 48 hours
old from discovery start. A restart loses that in-memory catalog; recovery then
uses available discovery page checkpoints or discovers again. The interrupted
page may be requested again. No retry continues halfway through Emby's
nontransactional import. The interrupted-import marker remains set until the
entire import and verification succeed.

Host operations that ignore cancellation can delay draining; another import
never starts while the prior import task is still running. Once all three
interruption retries are exhausted, the refresh reports **Error**, retains any
import repair requirement, and stops automatic recovery. A manual refresh starts
a new retry budget. Changing settings, cancelling, or starting a different
refresh discards the in-memory staged catalog.

**Limitation: import recovery is replay, not resumable import.** Neither the
referenced 4.9.1.90 SDK nor the published 4.11.0.5-beta6 SDK exposes a per-folder
import commit acknowledgment or resume cursor. Their
[`IChannelManager.RefreshChannelContent`](https://dev.emby.media/reference/pluginapi/MediaBrowser.Controller.Channels.IChannelManager.html)
accepts `maxRefreshLevel` and `restrictTopLevelFolderId`: it can restrict a run
to a top-level category, not resume at an arbitrary nested Music folder.
Successful discovery is retained in memory for whole-tree and single-category
import retries, not durably across a server restart. Page checkpoints recover
interrupted discovery, not completed database writes. TuneIn cannot skip
individual folders based on a confirmed database write.
Skipping served folders or returning shortened listings is not a safe remedy;
it could omit valid siblings during Emby reconciliation.

#### Category-scoped and progressive imports

With an intact, matching baseline, a single-category refresh imports only that
category. **Refresh all enabled categories** publishes one fully discovered
category at a time before discovering the next. Music runs last so other
categories can publish first, unless an interrupted Music import needs repair.
Incomplete categories are left
unchanged during a progressive run. Each verified import is saved before
discovery continues, so a later failure does not invalidate previously applied
categories. First builds, topology changes, incompatible checkpoints, and
unrelated interrupted imports use the full-tree path. An interrupted scoped
import is not automatically retried as a full-tree import. After a restart,
TuneIn may rediscover or replay an entire category; there is no within-category
cursor, and a served folder is not a commit.

These paths are fixture-tested, but have not been exercised on an isolated
Emby server.

While a staged import is stalled, the server logs the time since progress,
the unread folder count, and whether Emby is still importing or draining after
cancellation. Warnings are limited to three per attempt, no more than one per
minute. They do not identify individual folders or diagnose the cause of the stall.

The server log records automatic retries and per-attempt timings for discovery,
HTTP fetching, pacing, backoff, cached parsing, and import. Rate adaptation
does not guarantee provider acceptance or bypass TuneIn limits.

### Saved catalog and artwork

The saved catalog is the plugin's last successfully imported listing, not a
copy of Emby's database. Browsing can use it while a Control Panel refresh
discovers fresh data. Without a saved snapshot, browsing uses the provider.
Playback still resolves live and enforces current scopes. Availability during
Emby's import is not guaranteed.

Category folders use bundled artwork. Refreshing a category updates its saved
folder artwork even when the catalog is unchanged. Refresh also updates the
top-level TuneIn library item's Primary, Thumb, and first Backdrop images to
the **Powered by TuneIn** artwork.

## Playback

All configured categories share the playback resolver. Ordinary continuous
stations use direct playback. For TuneIn's recognized separate introduction
followed by live alternatives, the plugin plays the introduction and then
**one** working alternative as a single Emby media item. It does not expose
the introduction as a station, skip required introductions, or queue all
alternatives.

Recognition is limited to TuneIn's HTTPS
`tunein-ondemand.cdnstream1.com/multi_bump_sonic_pre_pre.mp3` introduction;
arbitrary short files are not treated as advertisements. Supported nested
M3U/PLS responses use the same rule, preserving URL case and query values.
If an individual nested playlist cannot be fetched or parsed, the resolver
can try a validated sibling. Resolution bounds and required introductions
still apply.

Separate-introduction playback uses the server-loopback
`/TuneIn/Playback/{signed-item}/{attempt}` endpoint and Emby's configured FFmpeg.
The older signed-item-only route still works. MP3/AAC
inputs are decoded to 48 kHz stereo PCM behind one WAV header, allowing mixed
codecs without ending the item. The host must allow FFmpeg child processes and
a local audio connection; decoding adds CPU cost. FFmpeg receives audio through
pipes, not remote URLs. Missing decoders or failed required introductions fail
explicitly.

Each media-source lookup resolves fresh provider information. A one-use,
unguessable handle bound to the signed item and its configuration allows the
matching loopback open to reuse that validated plan for up to 30 seconds; the
server retains at most 64 pending plans. Probes or opens without a reusable
plan resolve afresh rather than sharing broadcaster or introduction URLs.
Scope validation retains the Favorites username. Scope changes
cancel affected handoff sessions; stopping or leaving playback releases
responses and decoders. Failed live opening can try another advertised
alternative. Failures after selection terminate the stream without replaying
the introduction or cycling alternatives.

Each playlist is limited to 256 KiB and eight entries, with up to four nested
levels, 16 nested requests, and 16 audio opening attempts per handoff. Up to 16
resolution or handoff requests, including probes, can be active. Queue wait,
network opening, and playlist body reads have separate 30-second budgets
and distinct diagnostics. A provider HTTP 200 playlist with a failure
`#STATUS` and no URLs is reported separately from an empty playlist. Playback
diagnostics do not include provider response text, URLs, or tokens. Media
bodies are streamed rather than buffered in full.

Provider metadata shares the catalog pacer. Broadcaster playlists and relayed
audio openings use a separate four-slot limit and do not inherit TuneIn API
cooldowns. The plugin checks each redirect before following it; TuneIn API
requests cannot redirect off their origin, and playback fetches reject
HTTPS downgrades and nonpublic IP literals. This is not a network sandbox;
DNS and native direct-play connections remain subject to the host's network
policy. Handoff supports the recognized
MP3/AAC sequence, not arbitrary ad formats, DRM, or HLS sequencing. Ordinary
HLS destinations remain on Emby's native direct playback path.

## Build and test

The project references `MediaBrowser.Server.Core` **4.9.1.90** and
`System.Memory` **4.6.3**. Build with a .NET SDK:

```powershell
dotnet build MediaBrowser.Plugins.TuneIn\MediaBrowser.Plugins.TuneIn.csproj -c Release
```

Run the C# tests with a .NET 8 SDK:

```powershell
dotnet test MediaBrowser.Plugins.TuneIn.sln -c Release
```

Set `TUNEIN_TEST_FFMPEG` to a runnable FFmpeg executable to include the real
mixed-codec handoff and decoder/cancellation regressions. Those tests are
skipped without it; other playback tests use mocked responses and decoders.

Run the settings controller tests with Node.js:

```powershell
node --test MediaBrowser.Plugins.TuneIn.Tests\configPage.test.cjs
```

Tests cover configuration defaults and persistence, selectors, scope enforcement,
signed navigation, pagination, partial imports and retention, refresh recovery,
diagnostic bounds and redaction, station inventory, playback, native dashboard
lifecycle, form validation, and stale asynchronous responses. Automated results
do not establish compatibility with every Emby host or provider response.

The project is based on [MediaBrowser/TuneIn](https://github.com/MediaBrowser/TuneIn)
and is distributed under [LICENSE.md](LICENSE.md).
