# TuneIn for Emby

TuneIn adds an audio channel to Emby with administrator-configured browsing,
category refreshes, station counts, and live playback. Opening the settings
page or channel root does not recursively crawl the TuneIn catalog.

> [!IMPORTANT]
> **Compatibility and dependencies:** This branch targets Emby Server
> **4.9.1.90+ stable** or a compatible **4.9.2.7+ beta**, and builds against
> **MediaBrowser.Server.Core 4.9.1.90** and **System.Memory 4.6.3**.
> Do not copy dependency DLLs into Emby's plugin folder. Recognized
> separate-introduction playback also requires Emby's configured FFmpeg and
> loopback access; ordinary direct playback does not.

## Features

- **Configured browsing:** Choose category scopes and saved selections, with
  signed navigation IDs, lazy-loaded selectors, genre-folder filtering, and
  a lockable, theme-aware Control Panel. See [Configure browsing](#configure-browsing).
- **Resilient refreshes:** Refresh individual categories or all enabled
  categories, manually or on a schedule. Malformed trusted links can produce
  explicitly partial results with compatible saved branches retained; unsafe
  links and unrecovered failures still stop the affected work. See
  [Partial results](#partial-results).
- **Large catalogs and recovery:** Bounded parallel discovery, shared request
  pacing, checkpoints, compatible short-lived page reuse, scoped imports, and
  bounded retries help manage large refreshes. Import verification and
  reconciliation protect against treating interrupted imports as complete.
  See [Request limits and recovery](#request-limits-and-recovery).
- **Status and diagnostics:** The dashboard reports category outcomes, filtered
  and in-Emby station counts, progress, and bounded administrator diagnostic
  reports. See [Status and station counts](#status-and-station-counts).
- **Library maintenance:** Preview and clean one category or all categories
  with backup acknowledgement, protected media files, and a cooperative stop
  control. See [Clean up the TuneIn library](#clean-up-the-tunein-library).
- **Playback:** Direct playback and supported separate-introduction handoff
  include bounded resolution and stalled-read handling. See [Playback](#playback).

## Future features

### Native Emby language localization

Planned, not implemented. The Control Panel will follow the user's Emby
language preference for labels, buttons, descriptions, dialogs, and status
messages, with English fallback for unsupported languages or missing
translations. Configuration values and catalog identities will remain
unchanged. Native-library folder localization is outside the initial scope,
and compatibility with supported Emby versions still requires verification.

## Branch and release scope

This branch combines configured browsing, partial refreshes, the Control Panel,
playback improvements, and library maintenance. Discovery and import have
separate discovery and import deadlines: 1 to 10 hours for discovery (default: 3),
and 1 to 100 hours for import (default: 24).
Timeouts stop without automatic retries; bounded interruption recovery remains enabled. Restarting Emby alone
never starts a refresh.

## Requirements and installation

The plugin assembly version is **1.2.8.0**. Release downloads include
`build-info.json` to identify the source commit and `SHA256SUMS.txt` to verify
the files.

Download the [music refresh resilience RC1](https://github.com/oconnoradv/EmbyTuneIn/releases/tag/v1.2.8-music-refresh-resilience-rc.1).
If you downloaded an earlier RC1, download the replacement assets: the release
tag and assembly version are unchanged, but the source commit and checksums differ.
Check `build-info.json` when comparing a download with the branch.

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
selections to expose, keep **Enable TuneIn Station Filtering** enabled, and **Save**.

| Category | Required configuration |
| --- | --- |
| Music, By Language, By Location | Each defaults to All, or can use Selected only with saved choices |
| Local Radio | One source: all or selected cities/regions, or coordinates |
| Talk, Sports, Podcasts | Enable each category independently |
| My Favorites | A TuneIn username |

Local Radio preserves both source configurations but queries only the selected
one. If its source is ambiguous, choose regions or coordinates and save.
Other categories remain independent. Coordinates use decimal latitude and
longitude separated by a comma, within -90..90 and -180..180.

Music, By Language, By Location, and Local Cities and Regions use **All** when
no existing choices are saved. Existing saved choices retain **Selected only**.
Choose **Selected only** to browse choices from TuneIn; **Add** selects an
option, with up to 100 per selector. To remove choices, check them in the
Saved choices column, select **Delete Selected Items**, and confirm. Save
persists the changes; start Catalog Refresh to update the library.
Choices appear in a fixed-height scrolling panel. Temporary branch failures
retry once; incomplete branches are marked, and saved choices are preserved.
Talk, Sports, and Podcasts also default to **All** and support **Selected only**.
By Language follows TuneIn's group links for each selected language; those
groups are not additional language selections. The Music, Talk, and Sports
groups still respect their corresponding saved category scopes.

The **Settings** switch, beside **Global TuneIn Settings**, prevents accidental
edits and saves. The lock persists across browser sessions and server restarts.
**Save** locks settings after a successful save; switch it off to edit again.
The switch shows **Locked** in green or **Unlocked** in red.
Saving a changed setting in **TuneIn Station Filtering** opens **Catalog Refresh**
automatically.
Global and filtering settings, including the lock switch, cannot be changed while
a catalog refresh or cleanup is active, or while refresh status is unavailable.
Locking does not save pending changes or stop a refresh. **Station Selections** groups Music Genres, Languages, Locations,
Local Cities and Regions, Talk Topics, Sports & Leagues, and Podcast Topics. Collapsing it keeps unsaved choices; opening it
does not fetch from TuneIn by itself.
Buttons use the active user's native Emby theme.

### Global TuneIn Settings

This section opens before the first successful settings save. After a save it
defaults to collapsed and uses a green tint. **Enable TuneIn Station Filtering**
is on by default for a new install. An orange notice explains that Global
Settings must be reviewed and saved before refreshing. The checkbox unlocks
dependent editors immediately when settings are unlocked; filtering and catalog
refresh become available after you save.

- **Hide the individual genre folders** hides recognized genre shortcuts in
  Local Radio, By Location, Talk, Sports and Podcasts. It keeps provider-supplied
  **By Genre** folders and their contents, stations, popular listings and
  pagination. Music and By Language are unaffected. Refresh the affected
  categories after changing it.
- **Import valid results when catalog entries are skipped** permits partial
  imports of valid results when malformed trusted provider links are skipped.
  See [Partial results](#partial-results).
- **API request interval** sets minimum request spacing from 1 to 30 seconds,
  defaulting to 2 seconds between request starts when unset. Previously saved
  intervals are preserved. TuneIn retry delays take precedence.
- **Discovery deadline** sets the time allowed for discovery from 1 to 10
  whole hours, defaulting to 3 hours when absent from saved settings. It excludes
  import time and does not change individual HTTP response limits.
- **Import deadline** sets the time allowed per import from 1 to 100
  whole hours, defaulting to 24 hours when absent from saved settings. It appears
  beside Discovery deadline in the collapsed Advanced Settings subsection. It covers preparation,
  Emby traversal, and verification without changing discovery limits or import
  concurrency. Changing either deadline preserves compatible catalog and discovery snapshots.
  Previously saved deadline values are preserved rather than replaced by new defaults.

Both checkboxes default to checked when the settings are absent. Explicitly
saved unchecked choices remain unchanged. Changing these options does not
change existing navigation IDs.

Talk, Sports, and Podcasts default to **All**. Choose **Selected only** to load
options from TuneIn when the category is open. Saved choices appear beside the
browser controls, or below them on a narrow screen. Empty or stale selections
do not fall back to **All**.

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
Before the first refresh, the Control Panel highlights a reminder. It stays
dismissed after an administrator starts a refresh, including across restarts.

Discovery completes for a category before it is imported; eligible partial
and intentionally limited results are handled as described below. An initial or full-tree import supplies
Emby with the **complete staged channel tree**, including saved siblings. With
a compatible baseline, a refresh can import one complete category at a time.
A single-category refresh fetches only that category. Unchanged catalogs skip
import unless reconciliation is needed.

**Advanced Settings** includes **Max Station Count**, default **250**, accepting
**Not set** (blank) or **1-4000**. It counts unique TuneIn station IDs **per
top-level category**: all Music genres share one counter, and repeated placements
of the same station count once. Not set disables this configurable cap, not the
resource safety limits.

**Traversal depth limit** accepts **3-50**, default **5**. The TuneIn category
root is level **0**; every folder transition, including **More Stations**,
adds one level. Additional depths allow traversing more stations, subject to
Max Station Count. These controls limit TuneIn discovery, not Emby traversal.
Reaching either cap produces a coherent limited tree that can still be imported;
status reports **Limited**, rather than claiming complete discovery.

Refresh retains existing verified category entries still allowed by saved filters
and merges discovered additions and metadata updates. Absence from a discovery
result is not a deletion request. Existing placements retain their IDs when only
metadata changes. Existing Emby inventory is included, not just the last saved
snapshot; ambiguous or unverified ownership blocks unsafe preservation. Removing
selected genres or other saved filters still removes entries outside the allowed
scope. Changing the favorites username or coordinate-based Local Radio scope
invalidates that old scope. Retained totals can exceed Max Station Count, and
older deeper branches are preserved.

The import asks Emby to traverse the actual merged tree depth, including
retained branches and forward pagination folders. It is never clamped to the
new discovery depth. If Emby leaves folders unread, the refresh remains incomplete
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

After each successful **Save**, the panel reads Emby's Scheduled Tasks. If
**Refresh Internet Channels** has any triggers, a red notice at the top of the
panel asks you to remove them in **Advanced > Scheduled Tasks** so refreshes are
scheduled only from this Control Panel. The plugin never changes the task itself.

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

**TuneIn Station Filtering** is green and collapsed after filtering is enabled
and saved; otherwise it stays open for configuration. **Audio Categories** and
**Station Selections** collapse after settings are saved and start collapsed on later visits.
**Catalog Refresh** starts collapsed unless no refresh has ever started or a
refresh is active. You can expand or collapse it independently; its
heading keeps the current refresh state visible.
Its background is green when all enabled categories have successful results,
orange if any result is partial, and red if any has an error; errors take
precedence over partial results. The latest refresh's partial or error state is
also reflected, even if it did not reach a category. Unconfirmed or unavailable
results use a neutral surface. The heading includes the overall status in text;
individual category rows use the same colors and retain their own status text.
Live metrics show elapsed time, last progress, category progress, and catalog
pages against the safety limit. During import, progress switches to folders
served out of the known total. Discovery does not estimate a percentage or ETA
from the page cap. A served folder indicates that Emby requested its listing;
it does not confirm that Emby committed it to its database. Repeated requests
do not increase the unique folder count or update the last-progress time.

Import progress advances only on the first request for each staged folder.
Repeated requests still return the complete listing but do not reset the last
progress time or suppress stall warnings. Folders served measures provider
consumption, not confirmed Emby database commits.

The dashboard also shows station and subfolder entries in served versus total
staged listings, entries remaining in unserved listings, and direct content
counts for the last newly served folder. These are listing-delivery counts,
not unique stations or confirmed database writes: entries can repeat across
folders. Final counts remain visible after an import stops until a new refresh.

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
identifies whether an interruption recovery retry is for discovery or import.

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
The Maintenance panel follows the active Emby theme; its orange cleanup warning
appears when the panel is expanded.
The Maintenance and Catalog Refresh headers indicate when their work is active,
even while collapsed. Their amber activity highlights alternate in color unless reduced
motion is preferred.
Cleanup cannot be undone. **Backup Emby** requests the normal **Emby Server Backup**
scheduled task. It requires the [Backup & Restore plugin and Emby Premiere](https://emby.media/support/articles/Backup-Using-Plugin.html)
and uses your existing backup settings. TuneIn requires exactly one scheduled
task named **Emby Server Backup**. A request is not a completed backup: wait for
Emby to report success, or verify another backup separately if the task is unavailable.

Choose a category or **All categories**, then select **Preview cleanup** to count
generated folders and station entries verified under the TuneIn channel.
Category cleanup includes only matching authenticated category folders and their
verified descendants, including folders from earlier settings. Acknowledge that you verified a
successful backup, then confirm within five minutes to remove those entries in
background groups of up to 500 individually verified removal attempts, reporting
progress after each group, including failures. Up to four sibling entries can
be removed concurrently; children are still removed before their parents.
Unknown or unsigned entries are left alone; a folder with remaining children
is not deleted. The channel root, settings and media files are not cleanup
targets. Playlists and other references to removed entries may break.
Concurrent deletion behavior and speed have not been verified on a live Emby
server.

For a broken individual station, choose **One station entry** and paste its Emby
details URL or numeric/GUID item ID. **Preview cleanup** verifies its station type,
TuneIn ownership and ancestor chain, and identifies the exact entry. Confirm after
the backup acknowledgement to delete only that Emby item, not every placement of
the provider station. Its single saved parent placement is removed atomically
before deletion, protecting against immediate restoration from a stale snapshot;
if deletion fails, the saved placement is restored and failure is reported.
Parent folders and other placements remain. No exclusion or blacklist is saved:
future TuneIn discovery can add the entry again.

Refreshes (including schedules and recovery) and settings writes cannot run
while cleanup is active. The dashboard reports progress and partial failures.
**Stop cleanup** requests a cooperative stop after current removals finish;
already removed entries are not restored. While stopping, settings and refreshes
remain blocked. Preview again to remove remaining entries.
Before removal, cleanup clears recovery and discovery checkpoints. Single-category
cleanup preserves a compatible saved category baseline and records the cleaned
category before deleting anything, including across restart or partial failure.
The cleaned category requires complete discovery before it can be imported again;
partial discovery cannot restore its old snapshot. A category refresh can rebuild
it without rediscovering its siblings, although Emby may reconcile the channel
using those siblings' saved snapshots. If multiple enabled categories have been
cleaned, a refresh of all enabled categories may be required to rebuild them
without replaying stale snapshots. Full cleanup, or cleanup without a compatible
saved baseline, requires **Refresh all enabled categories** to rebuild the baseline.
Enabled categories can be recreated by a manual or scheduled refresh; disable a
category in settings if it should stay absent.

### Request limits and recovery

| Limit | Behavior |
| --- | --- |
| Discovery attempt | Configured deadline of 1 to 10 hours (default: 3), independent of import time |
| Import attempt | A fresh configured deadline of 1 to 100 hours (default: 24) for staging, importing and verification/finalization |
| Catalog cache | Up to 60,000 pages and 640 MiB per refresh |
| Discovered category | Up to 1,000,000 items and 1 GiB estimated data |
| Max Station Count | Not set or 1-4000 unique TuneIn station IDs per top-level category (default: 250); caps discovery, not retained totals |
| Catalog HTTP response | 90 seconds including body reads, excluding pacing and provider cooldowns |
| Discovery checkpoint | At a completed folder after five minutes or 3,000 additional station rows when new pages are available, and on interruption or timeout when possible |
| Automatic refresh retries | Up to 3 for interrupted work within an active refresh, shared across discovery and import |
| Timeout handling | Drains active work and stops without automatic discovery or import retries |
| Manual Cancel | Drains active work and stops; never triggers an automatic recovery retry |
| Checkpoint resume window | 48 hours from discovery start, with matching settings and requested category scope |
| TuneIn traversal depth | 3-50 (default: 5), category root at level 0; every folder and More Stations transition counts; limited results can be imported |

Discovery can overlap up to four independent requests without reducing shared spacing
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

Successful individual category refreshes retain up to 128 pages / 8 MiB for
ten minutes for a subsequent different category with matching settings. The
same category, refresh-all, recovery, and cleanup do not reuse that cache, and
partial or failed refreshes discard it. Live scope
validation is never replayed from it. The provider may change during that ten-minute window.
Large refresh limits can require substantial memory and disk: discovery
checkpoints can reach 900 MiB and saved catalog state can reach 1 GiB.

A discovery timeout saves additional validated pages when possible, drains active
work, and reports **TimedOut** without retrying automatically. A manual refresh
can reuse those pages within the 48-hour checkpoint window. An import deadline
also cancels and drains its operation, then reports **TimedOut** without an
automatic retry. The separate 90-second catalog HTTP response timeout also counts
as a discovery timeout.
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

Each interruption recovery attempt has fresh deadlines. Import recovery reuses the complete
staged catalog, including saved siblings and any Partial outcome, only while
it remains in memory, compatible with saved settings, and less than 48 hours
old from discovery start. A restart loses that in-memory catalog; recovery then
uses available discovery page checkpoints or discovers again. The interrupted
page may be requested again. No retry continues halfway through Emby's
nontransactional import. The interrupted-import marker remains set until the
entire import and verification succeed.

Host operations that ignore cancellation can delay draining; another import
never starts while the prior import task is still running. The dashboard shows
**Import deadline reached; draining** during this wait. The Emby server log
records when each import traversal starts and how many folders were served at
the deadline. The dashboard elapsed time covers the whole refresh, including
discovery, interruption retries, and draining. After an import timeout,
the refresh reports **TimedOut**, retains the
repair requirement, and makes a diagnostic report available. After a failed
import, manually refreshing the same scope replays the staged catalog without
discovery if it is still in memory, compatible with the saved settings, and
less than 48 hours old from discovery start. Changing settings, cancelling,
starting a different refresh, or restarting Emby discards that in-memory
catalog; the next refresh then runs discovery before repairing the library.
Once all three interruption retries are exhausted, the refresh reports
**Error**, retains any import repair requirement, and stops automatic recovery.
A manual refresh starts a new retry budget.

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
minute. With Advanced diagnostics enabled, each warning also includes up to 50
unserved folders with their names, bounded navigation paths, opaque references
and direct station/subfolder entry counts. Extra folders are explicitly reported
as omitted; details narrow as folders are served. The dashboard exposes the same
details and the stopped-refresh report retains the final snapshot. Deadline
logging also captures the unserved folders before cancellation drains. These
details do not diagnose the cause of the stall.

**Import recovery is replay, not resumable import.** Emby's
[`IChannelManager.RefreshChannelContent`](https://dev.emby.media/reference/pluginapi/MediaBrowser.Controller.Channels.IChannelManager.html)
does not provide this plugin with a per-folder commit acknowledgement or a
cursor for resuming a nested import. Replaying a complete tree avoids treating
served folders as committed or omitting siblings. Discovery checkpoints retain
provider pages, not completed Emby database writes.

The server log records automatic retries and per-attempt timings for discovery,
HTTP fetching, pacing, backoff, cached parsing, and import. Rate adaptation
does not guarantee provider acceptance or bypass TuneIn limits.

Global TuneIn Settings has a red-highlighted **Advanced Settings** subsection,
collapsed by default, containing API Request Interval, Import Deadline and
**Enable catalog identity diagnostics**. Diagnostics default to disabled.
Enable temporarily to investigate unexpected station replacement or slow imports,
then disable to avoid the additional comparison processing and memory overhead.
The same toggle enables verbose pending-folder diagnostics. Folder labels may
be private; review before sharing. URLs and signed navigation IDs are omitted,
and control characters are sanitized. Basic listing-content counts are always
available. Diagnostics do not exclude folders or change retry behavior.
Normal timeout and error logging is unaffected.

When enabled, after discovery produces a usable category listing, one aggregate identity
comparison is logged against its saved catalog (when available). It counts
unique retained, added and removed station IDs, retained stations with changed
external-ID, name, type or parent-folder sets, and added/removed
folder/external-ID memberships. Change counters can overlap; a station appearing
in several folders counts once in the station totals but has multiple memberships.
The log contains no station names, URLs or signed navigation IDs. It also labels
partial discovery and interrupted imports. This compares catalogs, not Emby's
database or individual deletion events, and does not change import behavior.
There is no per-station logging or periodic comparison during import.

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
Synchronous handoff reads time out after 60 seconds without audio and close
the stream; cancellation and stream disposal depend on the underlying host
and transport cooperating.

Provider metadata shares the catalog pacer. Broadcaster playlists and relayed
audio openings use a separate four-slot limit and do not inherit TuneIn API
cooldowns. The plugin checks each redirect before following it; TuneIn API
requests cannot redirect off their origin, and playback fetches reject
HTTPS downgrades and nonpublic IP literals. This is not a network sandbox;
DNS and native direct-play connections remain subject to the host's network
policy. **Deferred security work:** validate and pin public resolved addresses
for server-side playback connections and redirects to reduce DNS-based
server-side request forgery risk, while preserving TLS hostname verification.
Until then, use network egress controls to restrict access to internal services.
Handoff supports the recognized
MP3/AAC sequence, not arbitrary ad formats, DRM, or HLS sequencing. Ordinary
HLS destinations remain on Emby's native direct playback path.

## Manual testing

Use a test server and back it up before installing. Automated tests do not
establish live dashboard, import, authentication, or playback behavior.

- Open and reopen settings; check the theme, keyboard navigation, selector
  expansion, **Add**, saved choices, and retry feedback. Verify Local Radio
  uses only its selected source.
- Save settings and verify the first-refresh reminder, then start a refresh
  and confirm it stays dismissed after reopening settings and restarting Emby.
- Refresh all enabled categories to establish a baseline. Refresh one category,
  verify its siblings remain in Emby, and check the counts, artwork, status,
  and diagnostic report when applicable. Test partial results and retention
  with controlled provider failures; never assume a partial listing is complete.
- Verify schedules with the panel closed and confirm these actions do not
  create or trigger Emby Scheduled Tasks. Interrupt an import on the test
  server and check that a subsequent refresh reconciles the complete tree.
- Preview and clean one category, then test stopping a cleanup with work in
  progress; verify protected items and files remain and remaining entries can
  be cleaned after a new preview.
- Test ordinary and supported separate-introduction playback, stopping and
  changing stations, and disabling a relevant scope during playback.

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
and is distributed under [LICENSE.md](LICENSE.md), with bundled legacy
HtmlAgilityPack code under the
[Microsoft Public License](licenses/HtmlAgilityPack-MS-PL.txt).
See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for attribution and verified
source provenance. Release ZIPs include these license documents, and the
standalone plugin DLL embeds them as resources. Preserve them when redistributing.
