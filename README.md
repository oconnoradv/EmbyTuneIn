# TuneIn

TuneIn is an Emby audio channel. Browsing is configured explicitly in the
administrator settings page. Opening the settings page or channel root does not
crawl the TuneIn catalogue.

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
do not resolve streams or recursively crawl the catalogue; a failed branch can
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
Save your browsing settings, then open **Catalogue refresh**, which starts
collapsed. Opening it reads the latest
Control Panel status for the enabled categories; it does not start a refresh.
Choose **Refresh** beside a category to check only that category, or **Refresh
all enabled categories** to check them all. The status shows the latest outcomes
since Emby started, or a previous successful import from the saved checkpoint.

Use **Lock settings** at the top of the TuneIn page to prevent accidental
edits and saves. The lock persists on the server across browser sessions and
restarts. Turning it on does not save pending edits or stop catalogue refresh.
Only administrators can start, cancel, or read progress. Leaving the page stops
status polling, not the server run. Starting or cancelling is always explicit;
this plugin doesn't register, edit, or trigger anything in Scheduled Tasks.
Emby's built-in channel refresh still uses the shared catalogue browse path.
The plugin has no per-category outcome signal for that native task and does not
report a browse callback as a successful import.

The plugin caches its last successfully imported TuneIn catalogue for the
saved scopes, not a copy of Emby's library database. Browse can use that
snapshot while a Control Panel refresh discovers fresh data. Before the first
successful import there is no snapshot, so browsing follows the normal provider
path. Playback still resolves live and old IDs cannot bypass the saved scopes.

Refresh applies changes at the **category level**. It skips categories whose
fresh catalogue matches the last successful import. For a changed category,
the plugin sends Emby the **complete category listing**, with stable IDs for
unchanged stations. Emby's import API does not support station-by-station
deltas. Incomplete discovery never triggers an import for that category.

Category folders use the bundled TuneIn artwork. Emby may keep older folder
images after an upgrade; explicitly refresh the affected category to update
existing folder artwork, even when its catalogue is unchanged.

Emby's import isn't transactional. If cancellation, failure, or restart
interrupts an import, some metadata may already have changed. Refresh that
category or refresh all to reconcile it before refreshing a different
category on its own. Reconciliation imports the category again even if its
catalogue appears unchanged. Browse availability during Emby's import isn't
guaranteed.

### Manual testing

Use a test server. Confirm the top-level TuneIn library image shows the Powered
by TuneIn logo after the channel data version refreshes. If Emby retains older
cached artwork, refresh the TuneIn channel. After installing, open, leave, and
reopen TuneIn settings while a response is pending: the native transition and
loader should settle,
and stale responses should not update the page. Keyboard-open selected Location
and Language branches, try **Add** separately, and check saved paths and retry
feedback. With Local Radio on, alternate between saved regions and coordinates
and verify that Browse and Refresh use only the selected source. With legacy
settings enabling both, only Local Radio should be unavailable until corrected.

Expand **Catalogue refresh** and verify opening it updates the category states
without starting a run. Refresh one category and check that other categories
keep their status. Before and after a refresh, compare Scheduled Tasks for new entries, triggers,
or runs: none should have changed because of the Control Panel action. Establish
a successful baseline, browse it during another discovery, and check that
unchanged scopes are skipped and changed scopes still list unchanged stations.
Interrupt an import on the test server, restart, and check reconciliation on the
next explicit run. Separately run Emby's built-in channel refresh and verify
that it can browse the configured catalogue without changing Control Panel
status. These are host checks, not guarantees from the mocked tests.

## Build and test

> [!IMPORTANT]
> The Control Panel's scoped refresh requires `MediaBrowser.Server.Core`
> **4.9.1.90** at build time for Emby's folder-scoped import API. This is the
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

This plugin uses the upstream TuneIn catalogue and playback paths. It does not
include a protected stream relay or sequential pre-roll/live playback. Existing
upstream network and playlist playback limitations
therefore remain. No live Emby server authentication or dashboard runtime test
is implied by the automated build and tests.

The project is based on [MediaBrowser/TuneIn](https://github.com/MediaBrowser/TuneIn)
and is distributed under the terms in [LICENSE.md](LICENSE.md).
