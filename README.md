# TuneIn

TuneIn is an Emby audio channel. Browsing is configured explicitly in the
administrator settings page. Opening the settings page or channel root does not
crawl the TuneIn catalogue.

## Configure browsing

As an Emby administrator, open the TuneIn plugin settings, enable **configured
browsing after saving**, choose the categories to expose, and save.

- **Music**, **By Language**, and **By Location** require at least one enabled
  saved selection.
- **Local Radio** combines enabled saved cities or regions and optional
  coordinates. Coordinates are used only when their checkbox is enabled.
- **Talk**, **Sports**, and **Podcasts** have independent switches.
- **My Favorites** requires a TuneIn username.

Use **Load root**, **Expand**, and **Refresh current branch** to load selector
choices explicitly. Filtering applies only to labels already loaded. Selector
requests do not resolve or play streams and do not recursively crawl the
catalogue. A provider response may include station metadata; it is reported but
not played. Saved choices can be enabled or disabled individually.

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

## Build and test

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
selection scope enforcement, signed navigation, and playback-ID compatibility.
The Node tests cover the native settings lifecycle, visible Emby checkboxes,
selector laziness, form validation, and stale asynchronous responses.

## Limitations

This plugin uses the upstream TuneIn catalogue and playback paths. It does not
include a protected stream relay, sequential pre-roll/live playback, or a scoped
refresh task. Existing upstream network and playlist playback limitations
therefore remain. No live Emby server authentication or dashboard runtime test
is implied by the automated build and tests.

The project is based on [MediaBrowser/TuneIn](https://github.com/MediaBrowser/TuneIn)
and is distributed under the terms in [LICENSE.md](LICENSE.md).
