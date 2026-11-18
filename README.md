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
If TuneIn returns HTTP 429 during discovery, the refresh can retry one request
after `Retry-After` (or a 30-second cooldown if no valid header is supplied).
If TuneIn still rate-limits the run, no partial catalog is imported. Wait before
retrying; a slower API request interval can reduce repeat rate limiting.

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
and reuses saved listings for its siblings. If a sibling listing is missing
or the baseline is outdated, select **Refresh all enabled categories** to
build the baseline before retrying the individual refresh.
Incomplete discovery blocks the import, and unchanged catalogs skip import
unless the library needs reconciliation.
The error report identifies the category where discovery stopped.
Music discovery follows verified TuneIn genre aliases (such as Metal and Punk) for
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

This plugin uses the upstream TuneIn catalog and playback paths. It does not
include a protected stream relay or sequential pre-roll/live playback. Existing
upstream network and playlist playback limitations
therefore remain. No live Emby server authentication or dashboard runtime test
is implied by the automated build and tests.

The project is based on [MediaBrowser/TuneIn](https://github.com/MediaBrowser/TuneIn)
and is distributed under the terms in [LICENSE.md](LICENSE.md).
