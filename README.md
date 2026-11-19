# TuneIn

TuneIn is an Emby audio channel. Browsing is configured explicitly in the
administrator settings page. Opening the settings page or channel root does not
crawl the TuneIn catalog.

## Configure browsing

As an Emby administrator, open **Advanced → TuneIn** (also available from
**Plugins → TuneIn → Settings**), enable **configured browsing after saving**,
choose the categories to expose, and save.

- **Music**, **By Language**, and **By Location** require at least one enabled
  saved selection.
- **Local Radio** uses one source: enabled saved cities and regions, or valid
  coordinates. The other source is preserved but not queried. If older settings
  enabled both, Local Radio stays unavailable until you choose one source and
  save. This does not disable **By Location** or other configured categories.
- **Talk**, **Sports**, and **Podcasts** have independent switches.
- **My Favorites** requires a TuneIn username.

Open a selector and expand its branches to load choices on the first click.
Choices appear in a fixed-height scrolling panel; use **Add** on the right of a
row to save it without expanding it. By Language follows TuneIn's group links
for the selected language; those groups aren't extra language selections.
Saved paths stay visible and can be individually enabled or removed. Selectors
do not resolve streams or recursively crawl the catalog; a failed branch can
be retried without losing saved choices. An open branch automatically retries
once after a temporary request failure. An incomplete branch is marked.

Coordinates must be finite decimal latitude and longitude separated by a comma,
within -90..90 and -180..180 respectively. The server validates and normalizes
them using invariant culture. Up to 100 selections may be saved per selector.

Category and selection scopes are checked again when browsing and resolving
playback. This includes nested Music, Talk, and Sports items under By Language.
Navigation IDs are signed, bounded, and survive server restarts. Configuration
changes invalidate old IDs; return to the channel root after saving. Previously
imported library items can remain visible until Emby refreshes channel metadata,
but stale IDs cannot bypass current browsing or selection settings.

## Configuration migration

Existing configuration retains its TuneIn username and coordinate text. A
configuration without schema version 1 remains disabled: no categories or the
browsing gate are enabled automatically from the old username or coordinates.
Save the settings with the explicit choices and browsing opt-in to enable
browsing. Leaving the opt-in unchecked keeps the channel disabled.

The first saved configuration persists a server-generated navigation signing
key in the plugin configuration. Normal settings saves retain this key; deleting
or replacing it invalidates existing navigation IDs. The upstream plugin GUID,
assembly name, configuration filename, and assembly version remain unchanged.
The plugin thumbnail and TuneIn channel images use TuneIn's 544 × 306
**Powered by TuneIn** artwork; category folders retain their bundled artwork.

Legacy URL-based `subcat_`, `preset_`, and `stream_` item IDs are not accepted.
Old library entries may remain until Emby refreshes channel metadata; return to
the channel root to obtain current signed IDs.

## Refresh from the Control Panel

**Categories**, **By Language**, **By Location**, **Music genres**, and
**Local Radio** can each be collapsed without losing unsaved choices. These
sections start expanded before the first save and collapsed on later visits.
Save your browsing settings. **Catalog Refresh** opens expanded and can be
collapsed. Opening the panel reads status; it does not start a refresh.
Choose **Refresh** beside a category to check it, or **Refresh all enabled
categories** to check them all. Next to each category, choose **Off**, **Every 6
hours**, **Every 12 hours**, **Daily**, or **Weekly**, then select **Save**.
Schedules are off by default and run only for enabled categories. The interval
starts when the plugin detects the saved choice or Emby starts; missed runs
aren't replayed after downtime. Scheduled and manual runs never overlap.
In **Catalog browsing**, set the minimum API request interval from 1 to 30
seconds; the default is 3 seconds, and TuneIn's retry delays are always honored.
Previously saved intervals above 30 seconds are treated as 30 seconds.
If TuneIn returns HTTP 429 during discovery, the refresh retries the affected page
with a shared budget of three retries for the entire run. Consecutive rate limits
impose cooldowns of 30, 60, then 120 seconds; a longer `Retry-After` takes precedence.
Each catalogue rate limit also doubles the effective catalogue request interval,
up to 30 seconds. This learned minimum lasts until the server restarts, does not
change saved settings, and does not increase ordinary playback request intervals.
Successful catalogue responses reset the consecutive cooldown, not the learned pace.
Cancellation and the overall refresh timeout still apply during waits.
If the retry budget is exhausted, no partial catalog is imported. Wait before
retrying; adaptation cannot guarantee acceptance by TuneIn or bypass its limits.
The Control Panel shows the HTTP code and its meaning, for example
**HTTP 429 (Too Many Requests)**. If the run succeeds after retrying, the status
labels the earlier 429 as recovered and confirms that the refresh completed normally.

A malformed query or fragment on a trusted TuneIn child browse link or Music
station fallback URL does not stop Control Panel discovery of valid siblings.
If any links are skipped, the refresh reports
**Partial** and shows the total and counts grouped by category and validation
reason. It shows up to five groups and combines the rest into an “other” count.
Music can apply valid station and folder updates when it has a
compatible saved listing for every affected folder. The import retains missing
child folders and their saved contents where a malformed link may have hidden
them, and retains missing saved stations where a station URL was skipped.
If a safe merge is unavailable, no import or checkpoint update occurs.
Untrusted destinations and unrecovered provider request failures still fail
the refresh.

All Control Panel refreshes have up to 60 minutes for discovery and import
combined per attempt, with a shared 1,000-page limit regardless of which
categories are selected. During discovery, the plugin checkpoints validated
catalog pages separately from the imported library at the first completed
folder after each 20-minute interval. A discovery timeout is recorded and
automatically retried up to three times. Retries reuse checkpointed pages
without requesting them again; work since the last checkpoint may be repeated.
After a server restart, starting the same refresh can also reuse the checkpoint
if the settings still match and discovery began less than 24 hours ago.
Cancellation stops automatic retries, and an import timeout is not retried
automatically. The 8 MiB page cache limit and rate-limit backoff still apply.
A timeout during discovery does not import an incomplete tree.

The **Retrieve refresh log** button becomes available when the latest Control
Panel refresh fails. It retrieves a bounded error report for that run, not the
full Emby server log. The report is available until another refresh starts or
the server restarts, and may contain private server details; redact it before
sharing.

Use **Lock settings** at the top of the TuneIn page to prevent accidental
edits and saves. The lock persists on the server across browser sessions and
restarts. **Save** turns the switch on after the configuration is successfully
saved. Turn it off to edit again. Turning it on manually does not save pending
edits or stop catalog refresh.
Only administrators can configure schedules, start or cancel a run, or read
progress. Leaving the page stops status polling, not the server run. The plugin
owns these schedules and doesn't register, edit, or trigger any Emby Scheduled Tasks.
Emby's built-in channel refresh still uses the shared catalog browse path.
The plugin has no per-category outcome signal for that native task and does not
report a browse callback as a successful import.

The plugin caches its last successfully imported TuneIn catalog for the
saved scopes, not a copy of Emby's library database. Browse can use that
snapshot while a Control Panel refresh discovers fresh data. Before the first
successful import there is no snapshot, so browsing follows the normal provider
path. Playback still resolves live and old IDs cannot bypass the saved scopes.

Refresh discovers changes at the **category level**. If an import is needed,
the plugin sends Emby the **complete channel tree** so Emby doesn't drop sibling
categories. A single-category refresh discovers only the requested category
and reuses saved listings for its siblings. Saving additional enabled categories
preserves the completed status and listings of categories whose scopes haven't
changed. New categories can then be refreshed individually, including after a
server restart; categories not yet imported remain pending and aren't included
in the imported tree until their first successful refresh.
If an existing sibling's listing is missing or its scope changed, refresh that
category first, or use **Refresh all enabled categories**. An outdated baseline
from an earlier RC1 still requires a full refresh.
Incomplete discovery blocks the import unless Music can safely merge with a
saved listing as described above. Unchanged catalogs skip import unless the
library needs reconciliation.
The error report identifies the category where discovery stopped.
Music discovery follows verified TuneIn genre aliases (such as Smooth Jazz, Metal and Punk) for
station and show pagination, but rejects page links into unrelated genres.

After upgrading from an earlier RC1 with an outdated baseline, use **Refresh
all enabled categories** to rebuild it. During the Emby import, channel
callbacks receive the complete staged tree, including callbacks on separate workers.

Category folders use the bundled TuneIn artwork. Emby may keep older folder
images after an upgrade; explicitly refresh the affected category to update
existing folder artwork, even when its catalog is unchanged. The refresh also
updates the saved Primary, Thumb, and first Backdrop images on the top-level
TuneIn library item to **Powered by TuneIn**.

Emby's import isn't transactional. If cancellation, failure, or restart
interrupts an import, some metadata may already have changed. The next valid
refresh imports the complete catalog even if the requested listings are
unchanged. A category-only repair uses the saved sibling listings if the
baseline is current. Browse availability during Emby's import isn't guaranteed.

If a category refresh failed or an import was interrupted, selecting a
different category lets you **Retry previous first** (the new request waits)
or **Prioritize new refresh** (no separate retry is queued). An interrupted
import still needs the complete catalog repair whichever option you choose.
One refresh runs at a time, with up to two distinct requests waiting in order.
**Cancel refresh** cancels the active run and clears the waiting requests;
waiting requests also don't survive a server restart.

### Manual testing

Use a test server. Run **Refresh all enabled categories** from the TuneIn
Control Panel, then reload the Emby client. Confirm that the top-level library
tile shows **Powered by TuneIn** and category images are still distinct.
After a failed refresh, select another category, check both choices in the
prompt, and confirm the waiting list and buttons update as slots become free.
After installing, open, leave, and
reopen TuneIn settings while a response is pending: the native transition and
loader should settle,
and stale responses should not update the page. Keyboard-open selected Location
and Language branches, try **Add** separately, and check saved paths and retry
feedback. With Local Radio on, alternate between saved regions and coordinates
and verify that Browse and Refresh use only the selected source. With legacy
settings enabling both, only Local Radio should be unavailable until corrected.

Confirm **Catalog Refresh** opens expanded and can be collapsed. Refresh one
category after establishing a baseline and verify the other categories and
their stations still exist in Emby's library. Set one category's interval, save,
then enable two more categories and save again. Verify the previously refreshed
categories retain their completed status. Refresh each new category separately,
checking that only the requested category is fetched and existing library
categories remain. Restart between saves and refreshes to check persisted state.
Set a category interval again if needed, save,
and verify it refreshes with the Control Panel closed. Before and after a refresh, compare Scheduled Tasks for new entries, triggers,
or runs: none should have changed because of the Control Panel action. Establish
a successful baseline, browse it during another discovery, and check that
unchanged scopes are skipped and changed scopes still list unchanged stations.
Verify the API interval accepts 1 and 30 seconds, rejects values outside that
range, and delays requests accordingly. Cause a refresh error and retrieve its
report; verify the button is unavailable after a successful new run.
Interrupt an import on the test server, restart, and check reconciliation on the
next explicit run. Separately run Emby's built-in channel refresh and verify
that it can browse the configured catalog without changing Control Panel
status. These are host checks, not guarantees from the mocked tests.

## Pre-roll and live playback

All configured categories share the same playback resolver. When TuneIn returns
its recognized separate introduction followed by station alternatives, the plugin
plays the introduction and then **one** working live alternative as a single Emby
media item. It does not expose the short introduction as a separate station,
skip required introductions, or queue all alternative URLs. The verified
introduction is TuneIn's HTTPS
`tunein-ondemand.cdnstream1.com/multi_bump_sonic_pre_pre.mp3` asset; arbitrary
short audio files are not treated as advertisements. The rule also applies to
supported nested M3U/PLS lists, with URL case and query values preserved.

Ordinary stations, including continuous KEXP-style streams with in-stream
advertisements, keep their direct playback path. Only separate-introduction
stations use a server-loopback `/TuneIn/Playback/{signed-item}` endpoint.
It validates the saved category/selection scope and retains the Favorites
username. Probe and playback requests have independent lifetimes and re-fetch
provider playback information. Saving a changed browsing scope cancels affected
handoff sessions; leaving or stopping playback releases their responses and
decoders.

API and handoff media requests still use Emby's injected `IHttpClient` and shared
request pacing, not a new socket, proxy, DNS or TLS implementation. Handoff media
bodies are streamed, not buffered in full. For separate introductions, Emby's
configured FFmpeg decodes each MP3/AAC input to 48 kHz stereo PCM behind one WAV
header so an MP3 introduction can transition to AAC music without ending the
item or inheriting ad-only probe metadata. This uses additional server CPU and a
local audio connection; the host must supply a runnable FFmpeg executable and
allow its child processes. FFmpeg receives audio bytes through pipes, not remote
URLs. Missing decoders or failed required introductions fail explicitly rather
than silently dropping the introduction.

Each playlist is limited to 256 KiB and eight entries, with at most four nested
playlist levels, 16 nested playlist requests and 16 audio opening attempts per
handoff. Up to 16 handoff requests (including probes) can be active. Opening
timeouts allow 30 seconds plus the configured pacing interval; playlist body
reads have a 30-second budget. Failed live opening can try another advertised
alternative; read/decoder failures after selection terminate the stream without
restarting the ad or cycling alternatives.

On a test server, play asia DREAM and Big B Radio from Favorites and another
configured category. Confirm the introduction reaches sustained music without
advancing the playlist. Check KEXP, Stop, changing stations, leaving playback,
and saving a disabled category during playback. These host checks remain
necessary; automated codec and mocked API tests do not establish end-to-end
behavior on a live Emby installation.

## Build and test

> [!IMPORTANT]
> The Control Panel's scoped refresh requires `MediaBrowser.Server.Core`
> **4.9.1.90** at build time for Emby's channel import API. This is the
> version proposed in [MediaBrowser/TuneIn#3](https://github.com/MediaBrowser/TuneIn/pull/3);
> this branch pins it directly and does not depend on that PR merging.
> For manual testing, use Emby Server **4.9.1.90+ stable** or a compatible
> **4.9.2.7+ beta**. The plugin DLL's own version remains **1.2.8.0**.

Build the plugin with a .NET SDK:

```powershell
dotnet build MediaBrowser.Plugins.TuneIn\MediaBrowser.Plugins.TuneIn.csproj -c Release
```

Run the .NET tests with a .NET 8 SDK:

```powershell
dotnet test MediaBrowser.Plugins.TuneIn.sln -c Release
```

For the real MP3-introduction/AAC-live decoding regression, set
`TUNEIN_TEST_FFMPEG` to a runnable FFmpeg executable before running the .NET
tests. That regression is skipped when the variable is absent; the remaining
playback tests use mocked HTTP responses and decoders.

Run the settings controller tests with Node.js:

```powershell
node --test MediaBrowser.Plugins.TuneIn.Tests\configPage.test.cjs
```

The .NET tests cover configuration validation, lazy selectors, category and
selection scope enforcement, signed navigation, playback-ID compatibility,
and TuneIn pagination, including Location links with `filter=s:~#:country`.
The Node tests cover the native settings lifecycle, visible Emby checkboxes,
selector laziness, form validation, and stale asynchronous responses.

## Limitations

Connections, redirects, proxy behavior and destination protections remain the
responsibility of the installed Emby HTTP client/server; the local handoff is not
a replacement network sandbox. Keep the host patched. Separate-introduction
handoff supports the verified provider sequence and MP3/AAC audio, not arbitrary
ad formats, DRM or HLS sequencing. Ordinary HLS destinations remain on Emby's
native direct playback path. Unsupported required sequences fail rather than
skip ads. No live Emby authentication, playback or dashboard runtime validation
is implied by the automated build and tests.

The project is based on [MediaBrowser/TuneIn](https://github.com/MediaBrowser/TuneIn)
and is distributed under the terms in [LICENSE.md](LICENSE.md).
