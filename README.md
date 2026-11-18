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
assembly name, configuration filename, artwork, and assembly version remain
unchanged.

Legacy URL-based `subcat_`, `preset_`, and `stream_` item IDs are not accepted.
Old library entries may remain until Emby refreshes channel metadata; return to
the channel root to obtain current signed IDs.

## Refresh from the Control Panel

Save your browsing settings, then use **Catalogue refresh → Start refresh**.
Only administrators can start, cancel, or read progress. Leaving the page stops
status polling, not the server run. Starting or cancelling is always explicit;
this plugin doesn't register, edit, or trigger anything in Scheduled Tasks.
Emby's built-in channel refresh still uses the shared catalogue browse path.
The plugin has no per-category outcome signal for that native task and does not
report a browse callback as a successful import.

The Control Panel run checks each enabled scope against its last successful
import. An unchanged category is skipped. A changed category is imported as a
complete listing, not a delta; incomplete discovery never triggers an import
for that category. Browse can use the previous successful snapshot while
discovery runs. Before the first successful import there is no snapshot and
browsing follows the normal provider path. Playback still resolves live and
old IDs cannot bypass the saved scopes.

Emby's import isn't transactional. If cancellation, failure, or restart
interrupts an import, some metadata may already have changed. The next run
rechecks the dirty category and imports it again even if its catalogue appears
unchanged. Browse availability during Emby's import isn't guaranteed.

### Manual testing

Use a test server. After installing, open, leave, and reopen TuneIn settings
while a response is pending: the native transition and loader should settle,
and stale responses should not update the page. Keyboard-open selected Location
and Language branches, try **Add** separately, and check saved paths and retry
feedback. With Local Radio on, alternate between saved regions and coordinates
and verify that Browse and Refresh use only the selected source. With legacy
settings enabling both, only Local Radio should be unavailable until corrected.

Before and after a refresh, compare Scheduled Tasks for new entries, triggers,
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
